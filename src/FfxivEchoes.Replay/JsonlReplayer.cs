using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;
using FfxivEchoes.Replay.Capture;
using FfxivEchoes.Replay.MockServices;

namespace FfxivEchoes.Replay;

/// <summary>
/// 録画 jsonl を 1 行ずつパースして <see cref="IEventBus"/> に <see cref="IGameEvent"/> を publish する。
/// </summary>
/// <remarks>
/// <para>
/// 入力は <see cref="FfxivEchoes.Recording.EventSerializer"/> の出力スキーマと一致する形を想定する。
/// time は combat-relative 秒。各行 publish 前に <see cref="MockFramework"/> の internal clock を進める。
/// </para>
/// <para>
/// 未知の type は warn ログを出して skip する。設計書 §10「mock 網羅性不足」に対応するため、
/// 1 行のパース失敗で全体停止しない fail-soft 方式。
/// </para>
/// </remarks>
public sealed class JsonlReplayer
{
    private readonly IEventBus _bus;
    private readonly MockFramework _framework;
    private readonly MockObjectTable _objectTable;
    private readonly IPluginLog _log;
    private readonly TraceRecorder? _traceRecorder;

    /// <summary>録画 meta から zone 名・combat start 時刻を後段に渡すためのコールバック。</summary>
    public Action<RecordingMeta>? OnMetaParsed { get; set; }

    public JsonlReplayer(
        IEventBus bus,
        MockFramework framework,
        MockObjectTable objectTable,
        IPluginLog log,
        TraceRecorder? traceRecorder = null)
    {
        _bus = bus;
        _framework = framework;
        _objectTable = objectTable;
        _log = log;
        _traceRecorder = traceRecorder;
    }

    public int LinesProcessed { get; private set; }
    public int LinesSkipped { get; private set; }
    public DateTimeOffset CombatStart { get; private set; } = DateTimeOffset.UtcNow;

    public void Play(string recordingPath)
    {
        if (!File.Exists(recordingPath))
        {
            throw new FileNotFoundException($"Recording not found: {recordingPath}", recordingPath);
        }

        // combat_start を見つけるまでの仮基準時刻。実 combat_start で上書きする。
        CombatStart = DateTimeOffset.UtcNow;

        using var reader = new StreamReader(recordingPath);
        string? line;
        var lineNo = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                ProcessLine(line);
                LinesProcessed++;
            }
            catch (Exception ex)
            {
                LinesSkipped++;
                _log.Warning(ex, "[Replay] line {LineNo} parse failed: {Sample}", lineNo, Truncate(line, 120));
            }
        }

        _log.Information("[Replay] {File}: processed={N} skipped={S}",
            Path.GetFileName(recordingPath), LinesProcessed, LinesSkipped);
    }

    private void ProcessLine(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        if (root.TryGetProperty("meta", out var metaProp) && metaProp.GetBoolean())
        {
            HandleMeta(root);
            return;
        }

        if (!root.TryGetProperty("type", out var typeProp))
        {
            return;
        }

        var time = root.TryGetProperty("time", out var timeProp) ? timeProp.GetDouble() : 0.0;
        var timestamp = CombatStart.AddSeconds(time);

        // 仮想時計を進めて、サービスが Framework.Update を購読していれば呼ぶ。
        _framework.AdvanceTo(timestamp);

        var type = typeProp.GetString();
        IGameEvent? ev = type switch
        {
            "combat_start" => HandleCombatStart(timestamp),
            "combat_end" => new CombatEndedEvent(timestamp, ParseCombatEndReason(root)),
            "zone_change" => new ZoneChangedEvent(timestamp,
                root.TryGetProperty("territory_id", out var tid) ? tid.GetUInt32() : 0u,
                root.TryGetProperty("zone", out var zn) ? zn.GetString() ?? "Unknown" : "Unknown"),
            "cast_start" => ParseCastStart(root, timestamp),
            "cast_complete" => ParseCastComplete(root, timestamp),
            "cast_cancel" => ParseCastCancel(root, timestamp),
            "action_used" => ParseActionUsed(root, timestamp),
            "status_gain" => ParseStatusGain(root, timestamp),
            "status_lose" => ParseStatusLose(root, timestamp),
            "object_appear" => ParseObjectAppear(root, timestamp),
            "object_disappear" => ParseObjectDisappear(root, timestamp),
            "hp_change" => ParseHpChange(root, timestamp),
            "player_pos" => ParsePlayerPos(root, timestamp),
            _ => null,
        };

        if (ev is null)
        {
            _log.Warning("[Replay] unknown event type: {Type}", type ?? "(null)");
            LinesSkipped++;
            return;
        }

        // object_appear / object_disappear は MockObjectTable にも反映
        switch (ev)
        {
            case ObjectAppearedEvent oa:
                _objectTable.Register(oa);
                break;
            case ObjectDisappearedEvent od:
                _objectTable.Unregister(od.ObjectId);
                break;
        }

        // ジェネリック Publish<TEvent> を確実に「ランタイム型」で呼ぶため reflection 経由。
        // ev の宣言型は IGameEvent? だが、購読者は具体型 (CastStartedEvent 等) で待っている。
        DispatchByRuntimeType(ev);
    }

    private CombatStartedEvent HandleCombatStart(DateTimeOffset timestamp)
    {
        CombatStart = timestamp;
        _traceRecorder?.SetCombatStart(timestamp);
        return new CombatStartedEvent(timestamp);
    }

    private void HandleMeta(JsonElement root)
    {
        var zone = root.TryGetProperty("zone", out var z) ? z.GetString() : null;
        var startStr = root.TryGetProperty("start_time", out var st) ? st.GetString() : null;
        DateTimeOffset start;
        if (startStr is not null &&
            DateTimeOffset.TryParse(startStr, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var parsed))
        {
            start = parsed.ToUniversalTime();
        }
        else
        {
            start = DateTimeOffset.UtcNow;
        }
        CombatStart = start;
        _framework.AdvanceTo(start);
        _traceRecorder?.SetCombatStart(start);

        var meta = new RecordingMeta(zone, start);
        OnMetaParsed?.Invoke(meta);

        // meta 行は combat_start ではないので publish はしないが、zone は publish する
        // （プラグイン本体側のサブスクライバが zone を必要とするため）。
        if (!string.IsNullOrEmpty(zone))
        {
            DispatchByRuntimeType(new ZoneChangedEvent(start, 0, zone));
        }
    }

    private static CombatEndReason ParseCombatEndReason(JsonElement root)
    {
        if (!root.TryGetProperty("result", out var r)) return CombatEndReason.Unknown;
        return (r.GetString() ?? string.Empty).ToLowerInvariant() switch
        {
            "clear" => CombatEndReason.Cleared,
            "wipe" => CombatEndReason.Wiped,
            "left" => CombatEndReason.Left,
            _ => CombatEndReason.Unknown,
        };
    }

    private static CastStartedEvent ParseCastStart(JsonElement root, DateTimeOffset timestamp)
    {
        return new CastStartedEvent(
            timestamp,
            SourceId: GetUInt32(root, "source_id"),
            SourceName: GetString(root, "source") ?? string.Empty,
            CastActionId: ParseHexOrUint(root, "cast_id"),
            CastActionName: GetString(root, "cast_name") ?? string.Empty,
            CastTime: (float)(GetDouble(root, "cast_time") ?? 0.0),
            TargetId: GetNullableUInt32(root, "target_id"),
            TargetWorld: GetXyz(root, "target_"));
    }

    private static CastCompletedEvent ParseCastComplete(JsonElement root, DateTimeOffset timestamp)
        => new(timestamp,
            SourceId: GetUInt32(root, "source_id"),
            SourceName: GetString(root, "source") ?? string.Empty,
            CastActionId: ParseHexOrUint(root, "cast_id"),
            CastActionName: GetString(root, "cast_name") ?? string.Empty);

    private static CastCanceledEvent ParseCastCancel(JsonElement root, DateTimeOffset timestamp)
        => new(timestamp,
            SourceId: GetUInt32(root, "source_id"),
            SourceName: GetString(root, "source") ?? string.Empty,
            CastActionId: ParseHexOrUint(root, "cast_id"),
            CastActionName: GetString(root, "cast_name") ?? string.Empty);

    private static ActionUsedEvent ParseActionUsed(JsonElement root, DateTimeOffset timestamp)
        => new(timestamp,
            SourceId: GetUInt32(root, "source_id"),
            SourceName: GetString(root, "source") ?? string.Empty,
            ActionId: ParseHexOrUint(root, "action_id"),
            ActionName: GetString(root, "action_name") ?? string.Empty,
            TargetId: GetNullableUInt32(root, "target_id"),
            IsAutoAttack: root.TryGetProperty("auto_attack", out var aa) && aa.ValueKind == JsonValueKind.True,
            TargetWorld: GetXyz(root, "target_"));

    private static StatusGainedEvent ParseStatusGain(JsonElement root, DateTimeOffset timestamp)
        => new(timestamp,
            TargetId: GetUInt32(root, "target_id"),
            TargetName: GetString(root, "target") ?? string.Empty,
            StatusId: GetUInt32(root, "status_id"),
            StatusName: GetString(root, "status_name") ?? string.Empty,
            RemainingTime: (float)(GetDouble(root, "duration") ?? 0.0),
            Stacks: (ushort)(GetUInt32(root, "stacks")),
            SourceId: GetUInt32(root, "source_id"));

    private static StatusLostEvent ParseStatusLose(JsonElement root, DateTimeOffset timestamp)
        => new(timestamp,
            TargetId: GetUInt32(root, "target_id"),
            TargetName: GetString(root, "target") ?? string.Empty,
            StatusId: GetUInt32(root, "status_id"),
            StatusName: string.Empty);

    private static ObjectAppearedEvent ParseObjectAppear(JsonElement root, DateTimeOffset timestamp)
    {
        var pos = GetXyzObject(root, "position") ?? Vector3.Zero;
        return new ObjectAppearedEvent(
            timestamp,
            ObjectId: GetUInt32(root, "object_id"),
            ObjectName: GetString(root, "object_name") ?? string.Empty,
            DataId: GetUInt32(root, "data_id"),
            Position: pos,
            EntityId: GetNullableUInt32(root, "entity_id"));
    }

    private static ObjectDisappearedEvent ParseObjectDisappear(JsonElement root, DateTimeOffset timestamp)
        => new(timestamp,
            ObjectId: GetUInt32(root, "object_id"),
            ObjectName: GetString(root, "object_name") ?? string.Empty);

    private static HpChangedEvent ParseHpChange(JsonElement root, DateTimeOffset timestamp)
        => new(timestamp,
            ActorId: GetUInt32(root, "actor_id"),
            ActorName: GetString(root, "actor") ?? string.Empty,
            HpPct: (float)(GetDouble(root, "hp_pct") ?? 0.0),
            CurrentHp: GetUInt32(root, "hp"),
            MaxHp: GetUInt32(root, "hp_max"));

    private static LocalPlayerPositionEvent ParsePlayerPos(JsonElement root, DateTimeOffset timestamp)
        => new(timestamp, GetXyzObject(root, "position") ?? Vector3.Zero);

    private void DispatchByRuntimeType(IGameEvent ev)
    {
        var runtimeType = ev.GetType();
        var publish = typeof(IEventBus).GetMethod(nameof(IEventBus.Publish))!
            .MakeGenericMethod(runtimeType);
        publish.Invoke(_bus, new object[] { ev });
    }

    // ─── JSON 取得ヘルパ ───────────────────────────────────────────

    private static string? GetString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind == JsonValueKind.Null ? null : prop.GetString();
    }

    private static uint GetUInt32(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop)) return 0;
        if (prop.ValueKind == JsonValueKind.Null) return 0;
        if (prop.ValueKind == JsonValueKind.String)
        {
            return ParseHexOrUint(root, name);
        }
        return prop.TryGetUInt32(out var v) ? v : 0;
    }

    private static uint? GetNullableUInt32(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop)) return null;
        if (prop.ValueKind == JsonValueKind.Null) return null;
        return prop.TryGetUInt32(out var v) ? v : null;
    }

    private static double? GetDouble(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind == JsonValueKind.Null ? null : prop.GetDouble();
    }

    private static Vector3? GetXyz(JsonElement root, string prefix)
    {
        var xName = prefix + "x";
        var yName = prefix + "y";
        var zName = prefix + "z";
        if (!root.TryGetProperty(xName, out var xp) || xp.ValueKind == JsonValueKind.Null) return null;
        var x = (float)xp.GetDouble();
        var y = root.TryGetProperty(yName, out var yp) && yp.ValueKind != JsonValueKind.Null ? (float)yp.GetDouble() : 0f;
        var z = root.TryGetProperty(zName, out var zp) && zp.ValueKind != JsonValueKind.Null ? (float)zp.GetDouble() : 0f;
        return new Vector3(x, y, z);
    }

    private static Vector3? GetXyzObject(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var obj) || obj.ValueKind != JsonValueKind.Object) return null;
        var x = obj.TryGetProperty("x", out var xp) ? (float)xp.GetDouble() : 0f;
        var y = obj.TryGetProperty("y", out var yp) ? (float)yp.GetDouble() : 0f;
        var z = obj.TryGetProperty("z", out var zp) ? (float)zp.GetDouble() : 0f;
        return new Vector3(x, y, z);
    }

    private static uint ParseHexOrUint(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop)) return 0;
        if (prop.ValueKind == JsonValueKind.Number)
        {
            return prop.TryGetUInt32(out var v) ? v : 0;
        }
        if (prop.ValueKind == JsonValueKind.String)
        {
            var s = prop.GetString() ?? string.Empty;
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring(2);
                // sample-recording.jsonl は "0xINTRO" のような非 hex 識別子も使う。
                // hex でない場合は 0 を返し、ログだけ残す（呼び出し側で skip）。
                return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : 0;
            }
            return uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v2) ? v2 : 0;
        }
        return 0;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max) + "…";

    public sealed record RecordingMeta(string? Zone, DateTimeOffset Start);
}
