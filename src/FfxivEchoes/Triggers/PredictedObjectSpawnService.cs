using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;
using FfxivEchoes.Triggers.Models;

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

    private readonly IEventBus _bus;
    private readonly TriggerStore _store;
    private readonly IPluginLog _log;

    private readonly IDisposable _castStartSub;
    private readonly IDisposable _castCompleteSub;
    private readonly IDisposable _objectAppearSub;
    private readonly IDisposable _zoneSub;
    private readonly IDisposable _combatStartSub;
    private readonly IDisposable _combatEndSub;

    private string _currentZone = "Unknown";
    private bool _inCombat;

    // 発火中の予告：spawn.Id → 直近発火タイムスタンプ。短時間 dedup と
    // ObjectAppearedEvent 検知時の「予告→確定」格上げの dedup に使う。
    private readonly Dictionary<string, DateTimeOffset> _pendingPredictions = new();
    private readonly object _gate = new();

    public PredictedObjectSpawnService(IEventBus bus, TriggerStore store, IPluginLog log)
    {
        _bus = bus;
        _store = store;
        _log = log;

        _castStartSub = bus.Subscribe<CastStartedEvent>(OnCastStart);
        _castCompleteSub = bus.Subscribe<CastCompletedEvent>(OnCastComplete);
        _objectAppearSub = bus.Subscribe<ObjectAppearedEvent>(OnObjectAppeared);
        _zoneSub = bus.Subscribe<ZoneChangedEvent>(z =>
        {
            _currentZone = string.IsNullOrEmpty(z.ZoneName) ? "Unknown" : z.ZoneName;
            _inCombat = false;
            lock (_gate) { _pendingPredictions.Clear(); }
        });
        _combatStartSub = bus.Subscribe<CombatStartedEvent>(_ => _inCombat = true);
        _combatEndSub = bus.Subscribe<CombatEndedEvent>(_ =>
        {
            _inCombat = false;
            lock (_gate) { _pendingPredictions.Clear(); }
        });
    }

    public void Dispose()
    {
        _castStartSub.Dispose();
        _castCompleteSub.Dispose();
        _objectAppearSub.Dispose();
        _zoneSub.Dispose();
        _combatStartSub.Dispose();
        _combatEndSub.Dispose();
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

                FireSpawn(profile, spawn, sourceEvent);
            }
        }
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
                (DateTimeOffset.UtcNow - prev).TotalSeconds < DedupWindowSec)
            {
                return;
            }
            _pendingPredictions[spawn.Id] = DateTimeOffset.UtcNow;
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
                DurationSec = spawn.DelaySec + spawn.DurationSec,
                LiveFloorPaint = true,
            });
        }

        var action = new ActionDefinition
        {
            Type = "arena_view",
            Callout = $"予告: {spawn.ObjectName} ×{spawn.ObservedSpawnCount}",
            Duration = spawn.DelaySec + spawn.DurationSec,
            AoeZones = zones,
            ArenaRadius = profile.ArenaRadius,
            ArenaShape = profile.ArenaShape,
            ArenaWidth = profile.ArenaWidth,
            ArenaDepth = profile.ArenaDepth,
            ArenaCenterX = profile.ArenaCenterX,
            ArenaCenterZ = profile.ArenaCenterZ,
        };

        _bus.Publish(new TriggerFiredEvent(
            Timestamp: DateTimeOffset.UtcNow,
            Zone: _currentZone,
            TriggerId: $"__predicted_spawn_{spawn.Id}",
            TriggerName: $"予告: {spawn.TriggerCastName ?? "?"} → {spawn.ObjectName}",
            Actions: new[] { action },
            SourceEvent: sourceEvent));

        _log.Information(
            "[FfxivEchoes] PredictedObjectSpawn: 発火 cast={Cast} → {Name} ×{N} zones={Z} delay={Delay}s confidence={C}",
            spawn.TriggerCastName ?? "?", spawn.ObjectName, spawn.ObservedSpawnCount,
            zones.Count, spawn.DelaySec, spawn.Confidence);
    }

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
                .Where(k => k.Contains(dataIdHex, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var k in toRemove)
            {
                _pendingPredictions.Remove(k);
            }
        }
    }
}
