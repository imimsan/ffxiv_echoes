using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Dalamud.Plugin.Services;
using FfxivEchoes.Diagnostics;
using FfxivEchoes.Events;
using FfxivEchoes.Triggers.Models;
using IFramework = Dalamud.Plugin.Services.IFramework;

namespace FfxivEchoes.Triggers;

/// <summary>
/// <see cref="PredictedObjectSpawn"/> を起点に「cast 検知時点で先取り予告」を発火する。
/// <c>docs/predicted-object-spawn-design.md</c> §4 と一対一対応。
/// </summary>
/// <remarks>
/// 発火経路：CastStartedEvent / CastCompletedEvent を購読し、該当 spawn があれば
/// TriggerFiredEvent を publish。既存の ArenaViewHandler / ActorTrackedAoeService が
/// これを受けてミニマップ + 床塗りに描画する。
///
/// 実 ObjectAppearedEvent を受けたら pending から外して「確定」モードに格上げ
/// （位置補正は将来の改善）。
/// </remarks>
public sealed class PredictedObjectSpawnService : IDisposable
{
    /// <summary>同 spawn の連続発火を抑制する dedup ウィンドウ（秒）。</summary>
    private const double DedupWindowSec = 5.0;

    private readonly IFramework _framework;
    private readonly IEventBus _bus;
    private readonly TriggerStore _store;
    private readonly IPluginLog _log;

    private readonly IDisposable _castStartSub;
    private readonly IDisposable _castCompleteSub;
    private readonly IDisposable _castCancelSub;
    private readonly IDisposable _objectAppearSub;
    private readonly IDisposable _zoneSub;
    private readonly IDisposable _combatStartSub;
    private readonly IDisposable _combatEndSub;

    private string _currentZone = "Unknown";
    private bool _inCombat;

    // 発火中の予告：spawn.Id → 直近発火タイムスタンプ。短時間 dedup と
    // ObjectAppearedEvent 検知時の「予告→確定」格上げの dedup に使う。
    private readonly Dictionary<string, DateTimeOffset> _pendingPredictions = new();
    // 遅延発火スケジュール：cast 検知時点で「いつ発火するか」と発火対象 spawn を予約。
    // 毎フレーム時刻チェックして fireAt に達したら発火する。
    private readonly List<ScheduledSpawn> _scheduled = new();
    private readonly object _gate = new();

    /// <summary>
    /// 現在時刻を返す関数。本番は <see cref="DateTimeOffset.UtcNow"/> を返す既定値。
    /// Replay harness は MockFramework の仮想時刻を返す関数を注入して、
    /// jsonl 駆動で deterministic に scheduled spawn を発火させる。
    /// </summary>
    public Func<DateTimeOffset> NowProvider { get; set; } = () => DateTimeOffset.UtcNow;

    public PredictedObjectSpawnService(IFramework framework, IEventBus bus, TriggerStore store, IPluginLog log)
    {
        _framework = framework;
        _bus = bus;
        _store = store;
        _log = log;

        _castStartSub = bus.Subscribe<CastStartedEvent>(OnCastStart);
        _castCompleteSub = bus.Subscribe<CastCompletedEvent>(OnCastComplete);
        // 分岐ギミック（攻撃A/Bのどちらか）でキャストがキャンセルされたら、予約済み spawn を取り消す。
        // これが無いと来なかった方の add 予告（床塗り）が誤って発火し続ける。
        // ActorTrackedAoeService と対称のキャンセル処理。
        _castCancelSub = bus.Subscribe<CastCanceledEvent>(OnCastCanceled);
        _objectAppearSub = bus.Subscribe<ObjectAppearedEvent>(OnObjectAppeared);
        _zoneSub = bus.Subscribe<ZoneChangedEvent>(z =>
        {
            _currentZone = string.IsNullOrEmpty(z.ZoneName) ? "Unknown" : z.ZoneName;
            _inCombat = false;
            lock (_gate)
            {
                _pendingPredictions.Clear();
                _scheduled.Clear();
            }
        });
        _combatStartSub = bus.Subscribe<CombatStartedEvent>(_ => _inCombat = true);
        _combatEndSub = bus.Subscribe<CombatEndedEvent>(_ =>
        {
            _inCombat = false;
            lock (_gate)
            {
                _pendingPredictions.Clear();
                _scheduled.Clear();
            }
        });

        _framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
        _castStartSub.Dispose();
        _castCompleteSub.Dispose();
        _castCancelSub.Dispose();
        _objectAppearSub.Dispose();
        _zoneSub.Dispose();
        _combatStartSub.Dispose();
        _combatEndSub.Dispose();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            OnFrameworkUpdateCore();
        }
        catch (Exception ex)
        {
            FrameErrorThrottle.Report(_log, ex, "PredictedObjectSpawnService.OnFrameworkUpdate");
        }
    }

    private void OnFrameworkUpdateCore()
    {
        if (!_inCombat) return;
        var now = NowProvider();

        List<ScheduledSpawn>? due = null;
        lock (_gate)
        {
            for (var i = _scheduled.Count - 1; i >= 0; i--)
            {
                if (_scheduled[i].FireAt <= now)
                {
                    due ??= new List<ScheduledSpawn>();
                    due.Add(_scheduled[i]);
                    _scheduled.RemoveAt(i);
                }
            }
        }
        if (due is null) return;

        foreach (var item in due)
        {
            FireSpawn(item.Profile, item.Spawn, item.SourceEvent);
        }
    }

    private void OnCastStart(CastStartedEvent ev)
    {
        if (!_inCombat) return;
        FireSpawnsForEvent(ev.CastActionId, ev.CastActionName, ev.SourceName, "cast_start", ev);
    }

    private void OnCastComplete(CastCompletedEvent ev)
    {
        if (!_inCombat) return;
        FireSpawnsForEvent(ev.CastActionId, ev.CastActionName, ev.SourceName, "cast_complete", ev);
    }

    private void OnCastCanceled(CastCanceledEvent ev)
    {
        // キャンセルされたキャストに紐づく予約済み spawn を取り消す（来なかった分岐の誤予告防止）。
        int removed;
        lock (_gate)
        {
            removed = _scheduled.RemoveAll(s =>
                MatchesCast(s.Spawn, ev.CastActionId, ev.CastActionName, ev.SourceName));
        }
        if (removed > 0)
        {
            _log.Debug(
                "[FfxivEchoes] PredictedObjectSpawn: キャスト中断で予約取消 cast={Cast} 件数={N}",
                ev.CastActionName, removed);
        }
    }

    private void FireSpawnsForEvent(
        uint actionId,
        string actionName,
        string sourceName,
        string triggerEvent,
        IGameEvent sourceEvent)
    {
        var file = _store.GetByZone(_currentZone);
        if (file is null) return;

        foreach (var profile in file.StrategyProfiles)
        {
            if (!profile.Enabled) continue;
            if (profile.PredictedObjectSpawns is null || profile.PredictedObjectSpawns.Count == 0) continue;

            foreach (var spawn in profile.PredictedObjectSpawns)
            {
                if (!spawn.Enabled) continue;
                if (!string.Equals(spawn.TriggerEvent, triggerEvent, StringComparison.OrdinalIgnoreCase)) continue;
                if (!MatchesCast(spawn, actionId, actionName, sourceName)) continue;

                // 位置不定（ランダム出現）は予告描画しない。実出現後に AddObjectAoeService が
                // 描画する経路に任せる。
                if (!spawn.IsPositionStable)
                {
                    _log.Debug(
                        "[FfxivEchoes] PredictedObjectSpawn: 位置不定のため予告スキップ {Name}",
                        spawn.ObjectName);
                    continue;
                }
                if (spawn.Positions.Count == 0) continue;

                // 「Object 出現の LeadTimeSec 秒前」に発火するよう遅延スケジュール。
                // 出現 = cast から DelaySec 後 → 発火 = cast から (DelaySec - LeadTimeSec) 後。
                var leadTime = Math.Clamp(spawn.LeadTimeSec, 0, spawn.DelaySec);
                var fireAfterSec = Math.Max(0, spawn.DelaySec - leadTime);
                ScheduleSpawn(profile, spawn, sourceEvent, fireAfterSec);
            }
        }
    }

    private void ScheduleSpawn(StrategyProfile profile, PredictedObjectSpawn spawn, IGameEvent sourceEvent, double fireAfterSec)
    {
        var fireAt = NowProvider().AddSeconds(fireAfterSec);
        lock (_gate)
        {
            // 同 spawn の連続スケジュール防止：既に近い時刻のものがあれば skip
            if (_scheduled.Any(s => s.Spawn.Id == spawn.Id && (fireAt - s.FireAt).Duration().TotalSeconds < DedupWindowSec))
            {
                return;
            }
            _scheduled.Add(new ScheduledSpawn(profile, spawn, sourceEvent, fireAt));
        }
        _log.Information(
            "[FfxivEchoes] PredictedObjectSpawn: 予約 cast={Cast} → {Name} 発火まで {Sec:F1}s (delay={Delay}s lead={Lead}s)",
            spawn.TriggerCastName ?? "?", spawn.ObjectName, fireAfterSec, spawn.DelaySec, spawn.LeadTimeSec);
    }

    private static bool MatchesCast(PredictedObjectSpawn spawn, uint actionId, string actionName, string? sourceName)
    {
        // cast_id 優先、なければ cast_name で fallback
        if (!string.IsNullOrEmpty(spawn.TriggerCastId))
        {
            if (TryParseCastId(spawn.TriggerCastId, out var spawnId))
            {
                if (spawnId != actionId) return false;
            }
            else if (!string.Equals(spawn.TriggerCastName, actionName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        else if (!string.IsNullOrEmpty(spawn.TriggerCastName))
        {
            if (!string.Equals(spawn.TriggerCastName, actionName, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        else
        {
            return false;  // 起点 cast を識別できなければマッチさせない
        }

        if (!string.IsNullOrWhiteSpace(spawn.TriggerSourceName))
        {
            if (!string.Equals(spawn.TriggerSourceName, sourceName, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static bool TryParseCastId(string str, out uint id)
    {
        id = 0;
        if (string.IsNullOrEmpty(str)) return false;
        var s = str;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id);
    }

    private void FireSpawn(StrategyProfile profile, PredictedObjectSpawn spawn, IGameEvent sourceEvent)
    {
        lock (_gate)
        {
            if (_pendingPredictions.TryGetValue(spawn.Id, out var prev) &&
                (NowProvider() - prev).TotalSeconds < DedupWindowSec)
            {
                return;
            }
            _pendingPredictions[spawn.Id] = NowProvider();
        }

        var zones = new List<StrategyAoeZone>(spawn.Positions.Count);
        foreach (var pos in spawn.Positions)
        {
            zones.Add(new StrategyAoeZone
            {
                Shape = spawn.Shape,
                X = pos.X,
                Z = pos.Z,
                RadiusM = spawn.RadiusM,
                InnerRadiusM = spawn.InnerRadiusM,
                FanDeg = spawn.FanDeg,
                HalfWidthM = spawn.HalfWidthM,
                IsDanger = true,
                Color = spawn.Color,
                Anchor = "static",
                // FireSpawn は LeadTime 経過後（出現直前）に呼ばれるため、ここから表示する時間は
                // DurationSec のみ（DelaySec を足さない）。LeadTime + 出現後の数秒。
                DurationSec = spawn.DurationSec,
                LiveFloorPaint = true,
            });
        }

        var action = new ActionDefinition
        {
            Type = "arena_view",
            Callout = $"予告: {spawn.ObjectName} ×{spawn.ObservedSpawnCount}",
            Duration = spawn.DurationSec,
            AoeZones = zones,
            ArenaRadius = profile.ArenaRadius,
            ArenaShape = profile.ArenaShape,
            ArenaWidth = profile.ArenaWidth,
            ArenaDepth = profile.ArenaDepth,
            ArenaCenterX = profile.ArenaCenterX,
            ArenaCenterZ = profile.ArenaCenterZ,
        };

        _bus.Publish(new TriggerFiredEvent(
            Timestamp: NowProvider(),
            Zone: _currentZone,
            TriggerId: $"__predicted_spawn_{spawn.Id}",
            TriggerName: $"予告: {spawn.TriggerCastName ?? "?"} → {spawn.ObjectName}",
            Actions: new[] { action },
            SourceEvent: sourceEvent));

        _log.Information(
            "[FfxivEchoes] PredictedObjectSpawn: 発火 {Name} ×{Count} (cast={Cast}) zones={ZoneCount}",
            spawn.ObjectName, spawn.ObservedSpawnCount, spawn.TriggerCastName ?? "?", zones.Count);

        _log.Information(
            "[FfxivEchoes] PredictedObjectSpawn: 発火 cast={Cast} → {Name} ×{N} zones={Z} delay={Delay}s confidence={C}",
            spawn.TriggerCastName ?? "?", spawn.ObjectName, spawn.ObservedSpawnCount,
            zones.Count, spawn.DelaySec, spawn.Confidence);
    }

    private readonly record struct ScheduledSpawn(
        StrategyProfile Profile,
        PredictedObjectSpawn Spawn,
        IGameEvent SourceEvent,
        DateTimeOffset FireAt);

    private void OnObjectAppeared(ObjectAppearedEvent ev)
    {
        if (!_inCombat) return;
        // 実出現を検知したら同 spawn の pending を解除。
        // 詳細な「位置補正」「確定描画への置き換え」は将来改善（既存の AddObjectAoeService が
        // 実位置で描画するため、現状は予告と実描画が並列に存在する形）。
        lock (_gate)
        {
            var dataIdHex = ev.DataId.ToString("X");
            var toRemove = _pendingPredictions.Keys
                .Where(k => MatchesAppearedDataId(k, dataIdHex))
                .ToList();
            foreach (var k in toRemove)
            {
                _pendingPredictions.Remove(k);
            }
        }
    }

    /// <summary>
    /// spawn.Id（フォーマット <c>spawn_{castHex}_{dataIdHex}_{nameHash}</c>）の DataId フィールドが
    /// 出現イベントの DataId と完全一致するかを判定する。
    /// 旧実装の <c>k.Contains(dataIdHex)</c> は部分マッチで、短い hex の DataId が他 spawn の
    /// キー中に包含され無関係な予告を誤削除していた（P2-D）。アンダースコア区切りの完全一致で防ぐ。
    /// </summary>
    private static bool MatchesAppearedDataId(string spawnId, string dataIdHex)
    {
        if (string.IsNullOrEmpty(spawnId) || string.IsNullOrEmpty(dataIdHex))
        {
            return false;
        }
        var parts = spawnId.Split('_');
        // parts: ["spawn", castHex, dataIdHex, nameHash]
        if (parts.Length >= 4)
        {
            return string.Equals(parts[2], dataIdHex, StringComparison.OrdinalIgnoreCase);
        }
        // 想定外フォーマットは「_{dataIdHex}_」のセグメント完全包含で代替（部分マッチよりは厳格）。
        return spawnId.Contains("_" + dataIdHex + "_", StringComparison.OrdinalIgnoreCase);
    }
}
