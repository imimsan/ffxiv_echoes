using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FfxivEchoes.Capture;
using FfxivEchoes.Events;
using FfxivEchoes.Recording;
using FfxivEchoes.Triggers.Models;
using FfxivEchoes.Windows;

namespace FfxivEchoes.Triggers;

public sealed class PredictedCastReminderService : IDisposable
{
    private readonly IFramework _framework;
    private readonly IEventBus _bus;
    private readonly TriggerStore _store;
    private readonly CombatClock _combatClock;
    private readonly RecordingScanner _recordings;
    private readonly SyncOffsetTracker _syncOffset;
    private readonly IDataManager _dataManager;
    private readonly IObjectTable _objectTable;
    private readonly MinimapWindow _minimap;
    private readonly ActorTrackedAoeService? _actorTracked;
    private readonly IPluginLog _log;
    private readonly Func<string?, bool>? _branchActiveCheck;

    private readonly IDisposable _combatStartSub;
    private readonly IDisposable _combatEndSub;
    private readonly IDisposable _zoneSub;
    private readonly IDisposable _branchResolvedSub;

    private readonly List<PendingPrediction> _pending = new();
    private readonly object _gate = new();
    private string _currentZone = "Unknown";

    public PredictedCastReminderService(
        IFramework framework, IEventBus bus, TriggerStore store, CombatClock combatClock,
        RecordingScanner recordings, SyncOffsetTracker syncOffset,
        IDataManager dataManager, IObjectTable objectTable,
        WorldOverlayWindow worldOverlay, MinimapWindow minimap,
        IPluginLog log,
        ActorTrackedAoeService? actorTracked = null,
        Func<string?, bool>? branchActiveCheck = null)
    {
        _framework = framework;
        _bus = bus;
        _store = store;
        _combatClock = combatClock;
        _recordings = recordings;
        _syncOffset = syncOffset;
        _dataManager = dataManager;
        _objectTable = objectTable;
        _minimap = minimap;
        _actorTracked = actorTracked;
        _log = log;
        _branchActiveCheck = branchActiveCheck;

        _combatStartSub = bus.Subscribe<CombatStartedEvent>(_ => Schedule());
        _combatEndSub = bus.Subscribe<CombatEndedEvent>(_ =>
        {
            lock (_gate) { _pending.Clear(); }
        });
        _zoneSub = bus.Subscribe<ZoneChangedEvent>(z =>
            _currentZone = string.IsNullOrEmpty(z.ZoneName) ? "Unknown" : z.ZoneName);
        _branchResolvedSub = bus.Subscribe<BranchResolvedEvent>(_ => Schedule());

        _framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        _combatStartSub.Dispose();
        _combatEndSub.Dispose();
        _zoneSub.Dispose();
        _branchResolvedSub.Dispose();
        _framework.Update -= OnUpdate;
        lock (_gate) { _pending.Clear(); }
    }

    private void Schedule()
    {
        var file = _store.GetByZone(_currentZone);
        if (file is null)
        {
            return;
        }

        if (file.AutoSettings.PredictAdvanceWarningSec is not { } warn || warn <= 0)
        {
            return;
        }

        AggregatedEvents agg;
        try
        {
            agg = _recordings.Aggregate(_currentZone);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[FfxivEchoes] PredictedCastReminder: recording aggregate failed zone={Zone}", _currentZone);
            return;
        }

        var coveredCastIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var trig in file.Triggers)
        {
            if (trig.Match?.CastId is { Length: > 0 } cid)
            {
                coveredCastIds.Add(cid);
            }
        }

        var partyMembers = new HashSet<string>(_recordings.ListPartyMembers(_currentZone),
            StringComparer.OrdinalIgnoreCase);
        var predictions = RecordingPredictionPlanner.BuildCastPredictions(
            agg,
            coveredCastIds: null,
            partyMembers);

        lock (_gate)
        {
            _pending.Clear();
            foreach (var prediction in predictions)
            {
                var fireAt = CalculateFireAtSeconds(prediction, warn);
                var isCoveredByTrigger = !string.IsNullOrEmpty(prediction.CastId) &&
                    coveredCastIds.Contains(prediction.CastId);
                var strategy = StrategyPlanResolver.FindMechanicForPrediction(
                    file,
                    prediction,
                    _branchActiveCheck);
                var strategyActions = strategy.Profile is not null && strategy.Mechanic is not null
                    ? StrategyPlanResolver.BuildReminderActions(file, strategy.Profile, strategy.Mechanic)
                    : null;

                var actions = strategy.Mechanic?.AdvanceWarningSec is > 0 || isCoveredByTrigger
                    ? Array.Empty<ActionDefinition>()
                    : strategyActions;

                _pending.Add(new PendingPrediction(
                    prediction.Label,
                    prediction.CastId,
                    prediction.Source,
                    fireAt,
                    double.IsNaN(prediction.LatestObservedSeconds) ? prediction.RelativeSeconds : prediction.LatestObservedSeconds,
                    warn,
                    prediction.OccurrenceIndex,
                    actions));
            }
        }

        _log.Information("[FfxivEchoes] PredictedCastReminder: scheduled {Count} casts warn={Warn}s zone={Zone}",
            _pending.Count, warn, _currentZone);
    }

    private void OnUpdate(IFramework _)
    {
        var nowRel = _combatClock.RelativeSecondsAt(DateTimeOffset.UtcNow);
        if (nowRel is null)
        {
            return;
        }

        var offset = _syncOffset.CurrentOffsetSec;

        List<PendingPrediction>? toFire = null;
        lock (_gate)
        {
            for (var i = _pending.Count - 1; i >= 0; i--)
            {
                if (_pending[i].FireAtRelSec + offset <= nowRel.Value)
                {
                    if (ShouldFireDuePrediction(
                            _pending[i].FireAtRelSec,
                            _pending[i].EventAtRelSec,
                            offset,
                            nowRel.Value))
                    {
                        toFire ??= new List<PendingPrediction>();
                        toFire.Add(_pending[i]);
                    }
                    _pending.RemoveAt(i);
                }
            }
        }

        if (toFire is null)
        {
            return;
        }

        foreach (var p in toFire)
        {
            try
            {
                FirePrediction(p);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[FfxivEchoes] PredictedCastReminder failed label={Label}", p.Label);
            }
        }
    }

    private void FirePrediction(PendingPrediction p)
    {
        var file = _store.GetByZone(_currentZone);
        var match = BuildPredictionMatch(p);
        var pendingActions = p.Actions is null
            ? null
            : AutoSafeCallPlanner.RemoveMinimapActionsForRaidWideMatch(file, p.Actions, match);
        var suppressMinimap = AutoSafeCallPlanner.ShouldSuppressMinimap(file, match);
        if (AutoAoeDisplayPolicy.IsEnabled(file) && ShouldDrawAutoInferredPredictionVisual(suppressMinimap))
        {
            TryDrawPredictedMarker(file, p);
            TryEmitPredictedFloorPaint(p);
        }

        var actions = pendingActions is null
            ? new List<ActionDefinition>(BuildDefaultWarningActions(p.Label, p.AdvanceWarningSec))
            : new List<ActionDefinition>(pendingActions);
        if (actions.Count == 0)
        {
            return;
        }

        _bus.Publish(new TriggerFiredEvent(
            Timestamp: DateTimeOffset.UtcNow,
            Zone: _currentZone,
            TriggerId: $"__predicted_cast_{p.CastId}_{p.OccurrenceIndex}",
            TriggerName: $"予測: {p.Label}",
            Actions: actions,
            SourceEvent: new CombatStartedEvent(DateTimeOffset.UtcNow)));
    }

    /// <summary>
    /// PendingPrediction から actor を解決して、ActorTrackedAoeService の Predicted エントリを登録する。
    /// 床塗り経路は <see cref="ActorTrackedAoeService.TrackPredicted"/> が冪等性と dedup を保証する。
    /// </summary>
    private void TryEmitPredictedFloorPaint(PendingPrediction p)
    {
        if (_actorTracked is null) return;
        if (string.IsNullOrEmpty(p.CastId)) return;
        if (!AoeResolver.TryParseCastId(p.CastId, out var castId)) return;

        var source = ResolvePredictionSource(p.Source);
        if (source is null) return; // 床塗りは actor 必須（ミニマップは別経路で出る）

        // 予測着弾時刻：ev.EventAtRelSec は CombatStarted からの相対秒。
        // CombatClock 経由で絶対時刻に戻して TrackPredicted に渡す。
        var nowAbs = DateTimeOffset.UtcNow;
        var nowRel = _combatClock.RelativeSecondsAt(nowAbs);
        if (nowRel is null) return;
        var deltaSec = p.EventAtRelSec + _syncOffset.CurrentOffsetSec - nowRel.Value;
        var fireAt = nowAbs.AddSeconds(Math.Max(0, deltaSec));

        _actorTracked.TrackPredicted(source.EntityId, castId, p.Label, fireAt);
    }

    private static MatchCondition? BuildPredictionMatch(PendingPrediction prediction)
    {
        return string.IsNullOrWhiteSpace(prediction.CastId)
            ? null
            : new MatchCondition
            {
                CastId = prediction.CastId,
                CastName = prediction.Label,
            };
    }

    public static double CalculateFireAtSeconds(RecordingPrediction prediction, double warningSec)
    {
        var baseTime = double.IsNaN(prediction.EarliestObservedSeconds)
            ? prediction.RelativeSeconds
            : prediction.EarliestObservedSeconds;
        return Math.Max(0, baseTime - Math.Max(0, warningSec));
    }

    public static bool ShouldFireDuePrediction(
        double fireAtRelSec,
        double eventAtRelSec,
        double offsetSec,
        double nowRelSec,
        double staleGraceSec = 1.0)
    {
        if (fireAtRelSec + offsetSec > nowRelSec)
        {
            return false;
        }

        return eventAtRelSec + offsetSec >= nowRelSec - Math.Max(0, staleGraceSec);
    }

    public static IReadOnlyList<ActionDefinition> BuildDefaultWarningActions(string label, double warningSec)
    {
        var display = string.IsNullOrWhiteSpace(label) ? "Unknown" : label.Trim();
        var duration = Math.Max(1.0, warningSec);
        return new List<ActionDefinition>
        {
            new()
            {
                Type = "tts",
                Text = $"Next: {display}",
            },
            new()
            {
                Type = "overlay_text",
                Text = $"Next: {display}",
                Duration = 2.5,
                Color = "#FBBF24",
                Size = "large",
            },
            new()
            {
                Type = "timer_bar",
                Label = "Next",
                Duration = duration,
                Color = "#FBBF24",
                WarnAt = 3.0,
            },
        };
    }

    public static bool ShouldDrawAutoInferredPredictionVisual(bool suppressMinimap)
    {
        return !suppressMinimap;
    }

    private void TryDrawPredictedMarker(TriggerFile? file, PendingPrediction p)
    {
        AutoSafeCall? namedSafeCall = null;
        AoeResolver.AoeInfo? aoe = null;
        uint actionId = 0;
        if (!string.IsNullOrEmpty(p.CastId) &&
            AoeResolver.TryParseCastId(p.CastId, out actionId))
        {
            // 全体攻撃マーク済 → 予測通知でもミニマップは出さない
            if (AutoSafeCallPlanner.IsRaidWide(file, actionId, p.Label))
            {
                _log.Debug("[FfxivEchoes] PredictedReminder: skip raid-wide {Name} id={Id:X4}", p.Label, actionId);
                return;
            }
            namedSafeCall = AutoSafeCallPlanner.CreateKnown(actionId, p.Label);
            aoe = AoeResolver.Resolve(_dataManager, actionId, _log);
            // Lumina の EffectRange だけで全体攻撃判定（学習なしでも 1 戦目から効く）
            // - 円形 25m 以上 → ほぼ確実にアリーナ全体
            // - それ以外の形（ドーナツ等）でも 30m 以上はもう「内側に逃げる時間が無い」レベル
            //   なので回避不能扱いにしてミニマップに出さない
            if (aoe is not null &&
                ((aoe.CastType is 2 or 5 && aoe.Radius >= 25f) || aoe.Radius >= 30f))
            {
                _log.Information(
                    "[FfxivEchoes] PredictedReminder: skip oversized AoE {Name} radius={R}m castType={Ct} (全体扱い)",
                    p.Label, aoe.Radius, aoe.CastType);
                return;
            }
        }
        else
        {
            namedSafeCall = AutoSafeCallPlanner.CreateKnownByName(p.Label);
        }

        var safeCall = namedSafeCall ?? (aoe is not null
            ? AutoSafeCallPlanner.Create(aoe, p.Label)
            : null);
        var visualCall = safeCall ?? (aoe is not null
            ? AutoSafeCallPlanner.CreateVisual(aoe, p.Label)
            : null);
        if (visualCall is null)
        {
            return;
        }
        safeCall ??= visualCall;

        var source = ResolvePredictionSource(p.Source);
        // source actor を解決できない場合はミニマップ描画スキップ。sourceWorld=null で
        // AddArenaView を呼ぶと MinimapWindow.DrawActualAoeShape が origin=mapCenter
        // （= アリーナ中央 (100, 100) ≒ DefaultArenaCenter）にフォールバックし、
        // 「マップ中央に正体不明のドーナツ」が描画されてしまう。床塗り側 (TryEmitPredictedFloorPaint
        // L251) は既に source 必須ガードがあるが、ミニマップ側にも同じガードを入れて二重防御。
        // 月の底のケラノウス・エイドロン (0x67E1) は source 名が変身演出で
        // ObjectTable から actor を引けないケースが典型で、ここに該当する。
        if (source is null)
        {
            _log.Debug("[FfxivEchoes] PredictedReminder: skip source 未解決 {Name} ({Source})",
                p.Label, p.Source ?? "?");
            return;
        }
        var sourceWorld = new Vector3(source.Position.X, source.Position.Y, source.Position.Z);
        var radius = aoe is null
            ? (float?)null
            : AoeResolver.EffectiveRadius(aoe, source?.HitboxRadius ?? 0f);
        var facingAngleRad = ArenaProjection.UsesFacing(visualCall.Gimmick) && source is not null
            ? ArenaProjection.RotationToMapAngleRad(source.Rotation)
            : (float?)null;

        var arena = AutoAoeDisplayPolicy.ResolveArena(file);
        _minimap.AddArenaView(
            gimmick: visualCall.Gimmick,
            callout: $"次: {safeCall.Callout}",
            // 予測通知 → 実発動 → さらに発動後 3 秒残す（「すぐ消えると困る」）
            durationSec: p.AdvanceWarningSec + 5.0 + 3.0,
            direction: ArenaProjection.UsesFacing(visualCall.Gimmick) ? "N" : null,
            fanDeg: visualCall.FanDeg,
            arenaRadius: arena.ArenaRadius,
            directionAngleRad: facingAngleRad,
            sourceWorld: sourceWorld,
            aoeRadius: radius,
            aoeCastType: aoe?.CastType,
            aoeOmenId: aoe?.OmenId,
            arenaShape: arena.ArenaShape,
            arenaWidth: arena.ArenaWidth,
            arenaDepth: arena.ArenaDepth,
            lockedArenaCenter: arena.LockedArenaCenter);

        _log.Information("[FfxivEchoes] Predicted minimap telegraph: {Label} gimmick={G} source={Source}",
            p.Label, visualCall.Gimmick, p.Source ?? "?");
    }

    private IBattleNpc? ResolvePredictionSource(string? sourceName)
    {
        IBattleNpc? fallback = null;
        uint fallbackHp = 0;
        foreach (var obj in _objectTable)
        {
            if (obj is not IBattleNpc npc) continue;
            if (npc.MaxHp == 0) continue;
            if (IsPet(npc)) continue;

            if (!string.IsNullOrEmpty(sourceName) &&
                string.Equals(npc.Name.TextValue, sourceName, StringComparison.OrdinalIgnoreCase))
            {
                return npc;
            }

            if (npc.MaxHp > fallbackHp)
            {
                fallbackHp = npc.MaxHp;
                fallback = npc;
            }
        }

        return fallback;
    }

    private static bool IsPet(IBattleNpc npc)
    {
        try
        {
            return npc.BattleNpcKind == BattleNpcSubKind.Pet;
        }
        catch
        {
            return false;
        }
    }

    private readonly record struct PendingPrediction(
        string Label,
        string CastId,
        string? Source,
        double FireAtRelSec,
        double EventAtRelSec,
        double AdvanceWarningSec,
        int OccurrenceIndex,
        IReadOnlyList<ActionDefinition>? Actions);
}
