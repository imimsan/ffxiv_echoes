using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FfxivEchoes.Recording;
using FfxivEchoes.Triggers;

namespace FfxivEchoes.Commands.Handlers;

/// <summary>
/// <c>/echoes dump-actions [zone]</c>。指定ゾーン（または全ゾーン）のトリガーファイル + 録画
/// から登場する action_id を全列挙し、Lumina から形状情報を引いて
/// <c>data/actions/&lt;zone&gt;.json</c> に書き出す。
/// </summary>
/// <remarks>
/// <para>
/// 設計書 <c>docs/superpowers/specs/2026-05-24-aoe-fix-and-browser-replay-design.md §5</c>。
/// 実機 Dalamud から 1 度だけ実行して JSON をリポジトリにコミットしておけば、
/// <c>FfxivEchoes.Replay</c>（headless）でも同じ形状情報を参照できる。
/// </para>
/// <para>
/// 出力先のリポジトリルートは「アセンブリ位置から上に <c>data/actions</c> を持つ
/// ディレクトリ」or「<c>FfxivEchoes.sln</c> があるディレクトリ」を探す。
/// 見つからない場合は ConfigDirectory/data/actions/ にフォールバックする。
/// </para>
/// </remarks>
public sealed class DumpActionsCommand : ICommandHandler
{
    public string Verb => "dump-actions";
    public string Usage => "dump-actions [zone]";
    public string Description => "trigger と録画から action_id を集め、Lumina 形状を JSON dump（data/actions/<zone>.json）";

    private readonly TriggerStore _store;
    private readonly RecordingScanner _recordings;
    private readonly IActionLookup _actionLookup;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IChatGui _chat;
    private readonly IPluginLog _log;

    public DumpActionsCommand(
        TriggerStore store,
        RecordingScanner recordings,
        IActionLookup actionLookup,
        IDalamudPluginInterface pluginInterface,
        IChatGui chat,
        IPluginLog log)
    {
        _store = store;
        _recordings = recordings;
        _actionLookup = actionLookup;
        _pluginInterface = pluginInterface;
        _chat = chat;
        _log = log;
    }

    public void Execute(string args)
    {
        var zoneArg = (args ?? string.Empty).Trim();
        var allZones = _store.Snapshot().Keys
            .Concat(_recordings.ListZonesWithRecordings())
            .Where(z => !string.IsNullOrWhiteSpace(z))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(z => z, StringComparer.OrdinalIgnoreCase)
            .ToList();

        IReadOnlyList<string> targetZones;
        if (string.IsNullOrEmpty(zoneArg))
        {
            if (allZones.Count == 0)
            {
                _chat.PrintError("[FFXIV Echoes] dump-actions: 対象 zone が無い（trigger / 録画とも）");
                return;
            }
            targetZones = allZones;
        }
        else
        {
            var hit = allZones.FirstOrDefault(z => string.Equals(z, zoneArg, StringComparison.OrdinalIgnoreCase));
            targetZones = hit is null
                ? new[] { zoneArg }
                : new[] { hit };
        }

        var outDir = ResolveOutputDirectory();
        try
        {
            Directory.CreateDirectory(outDir);
        }
        catch (Exception ex)
        {
            _chat.PrintError($"[FFXIV Echoes] dump-actions: 出力ディレクトリ作成失敗 {outDir} - {ex.Message}");
            _log.Error(ex, "[FfxivEchoes] dump-actions: CreateDirectory 失敗 {Dir}", outDir);
            return;
        }

        var totalWritten = 0;
        foreach (var zone in targetZones)
        {
            try
            {
                var ids = CollectActionIds(zone);
                if (ids.Count == 0)
                {
                    _chat.Print($"[FFXIV Echoes] dump-actions: {zone} → action_id が見つからずスキップ");
                    continue;
                }

                var path = Path.Combine(outDir, SanitizeFileName(zone) + ".json");
                var written = WriteDump(path, zone, ids);
                _chat.Print($"[FFXIV Echoes] dump-actions: {zone} → {written} action を {path} に保存");
                _log.Information(
                    "[FfxivEchoes] dump-actions: zone={Zone} actions={N} path={Path}",
                    zone, written, path);
                totalWritten++;
            }
            catch (Exception ex)
            {
                _chat.PrintError($"[FFXIV Echoes] dump-actions: {zone} 失敗 - {ex.Message}");
                _log.Error(ex, "[FfxivEchoes] dump-actions: zone={Zone} 失敗", zone);
            }
        }

        _chat.Print($"[FFXIV Echoes] dump-actions: 完了。{totalWritten}/{targetZones.Count} zone を書き出し");
    }

    /// <summary>
    /// 指定ゾーンのトリガーファイル + 全録画 jsonl から登場する action_id を集める。
    /// </summary>
    private HashSet<uint> CollectActionIds(string zone)
    {
        var ids = new HashSet<uint>();

        // 1. トリガーファイル：JsonDocument で再帰的に "cast_id" / "action_id" を拾う。
        //    モデル経由で取りこぼすより、生 JSON を素朴に走査する方が schema 変化に強い。
        try
        {
            var file = _store.GetByZone(zone);
            if (file is not null)
            {
                var loaderDir = Path.Combine(_pluginInterface.ConfigDirectory.FullName, "triggers");
                var path = Path.Combine(loaderDir, SanitizeFileName(zone) + ".json");
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    using var doc = JsonDocument.Parse(json);
                    CollectFromJsonElement(doc.RootElement, ids);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[FfxivEchoes] dump-actions: trigger 読み取り失敗 zone={Zone}", zone);
        }

        // 2. 録画 jsonl：cast_start.cast_id / action_used.action_id を line-by-line で拾う。
        try
        {
            foreach (var rec in _recordings.ListRecordings(zone))
            {
                CollectFromRecordingFile(rec.Path, ids);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[FfxivEchoes] dump-actions: 録画読み取り失敗 zone={Zone}", zone);
        }

        return ids;
    }

    private void CollectFromJsonElement(JsonElement element, HashSet<uint> ids)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.NameEquals("cast_id") || prop.NameEquals("action_id"))
                    {
                        TryAddId(prop.Value, ids);
                    }
                    else
                    {
                        CollectFromJsonElement(prop.Value, ids);
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectFromJsonElement(item, ids);
                }
                break;
        }
    }

    private static void TryAddId(JsonElement value, HashSet<uint> ids)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            if (TryParseHexId(value.GetString(), out var id))
            {
                ids.Add(id);
            }
        }
        else if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var num))
        {
            ids.Add(num);
        }
    }

    private void CollectFromRecordingFile(string path, HashSet<uint> ids)
    {
        try
        {
            // 録画は書き込み中の可能性もあるので共有読み取り。
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    if (root.TryGetProperty("cast_id", out var castEl)) TryAddId(castEl, ids);
                    if (root.TryGetProperty("action_id", out var actEl)) TryAddId(actEl, ids);
                }
                catch (JsonException)
                {
                    // 壊れた行はスキップ
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[FfxivEchoes] dump-actions: 録画読み込みに失敗 {Path}", path);
        }
    }

    /// <summary>
    /// 集めた action_id について <see cref="IActionLookup"/> で形状を引いて JSON 出力。
    /// </summary>
    /// <returns>書き出した action 件数（Lumina に見つかったもの）。</returns>
    private int WriteDump(string outPath, string zone, IReadOnlyCollection<uint> ids)
    {
        var actions = new List<Dictionary<string, object?>>();
        foreach (var id in ids.OrderBy(i => i))
        {
            var geom = _actionLookup.TryGet(id);
            if (geom is null)
            {
                continue;
            }
            actions.Add(new Dictionary<string, object?>
            {
                ["id"] = $"0x{id:X4}",
                ["id_dec"] = id,
                ["name"] = geom.Name,
                ["cast_type"] = geom.CastType,
                ["effect_range"] = geom.EffectRangeM,
                ["x_axis_modifier"] = geom.XAxisModifierM,
                ["omen"] = geom.OmenId == 0 ? null : geom.OmenId.ToString(CultureInfo.InvariantCulture),
                ["cast_time_ms"] = geom.CastTimeMs,
                ["is_player_action"] = geom.IsPlayerAction,
            });
        }

        var doc = new Dictionary<string, object?>
        {
            ["zone"] = zone,
            ["generated_at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["source_action_count"] = ids.Count,
            ["actions"] = actions,
        };
        var opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
        File.WriteAllText(outPath, JsonSerializer.Serialize(doc, opts));
        return actions.Count;
    }

    /// <summary>
    /// data/actions/ の出力先を解決。リポジトリのルート（.sln 同居）を優先。
    /// 見つからなければ ConfigDirectory/data/actions/ にフォールバック。
    /// </summary>
    private string ResolveOutputDirectory()
    {
        var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (!string.IsNullOrEmpty(asmDir))
        {
            var current = new DirectoryInfo(asmDir);
            while (current is not null)
            {
                var slnHit = Path.Combine(current.FullName, "FfxivEchoes.sln");
                var dataHit = Path.Combine(current.FullName, "data", "actions");
                if (File.Exists(slnHit) || Directory.Exists(dataHit))
                {
                    return Path.Combine(current.FullName, "data", "actions");
                }
                current = current.Parent;
            }
        }
        return Path.Combine(_pluginInterface.ConfigDirectory.FullName, "data", "actions");
    }

    private static bool TryParseHexId(string? spec, out uint id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(spec)) return false;
        var s = spec.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (s.StartsWith("#", StringComparison.Ordinal)) s = s[1..];
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id);
    }

    private static string SanitizeFileName(string s)
    {
        if (string.IsNullOrEmpty(s)) return "Unknown";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }
        return sb.ToString();
    }
}
