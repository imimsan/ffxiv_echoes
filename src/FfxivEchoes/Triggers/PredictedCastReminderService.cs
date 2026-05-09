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
    private readonly IPluginLog _log;

    private readonly IDisposable _combatStartSub;
    private readonly IDisposable _combatEndSub;
    private readonly IDisposable _zoneSub;

    private readonly List<PendingPrediction> _pending = new();
    private readonly object _gate = new();
    private string _currentZone = "Unknown";

    public PredictedCastReminderService(
        IFramework framework, IEventBus bus, TriggerStore store, CombatClock combatClock,
        RecordingScanner recordings, SyncOffsetTracker syncOffset,
        IDataManager dataManager, IObjectTable objectTable,
        WorldOverlayWindow worldOverlay, MinimapWindow minimap,
        IPluginLog log)
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
        _log = log;

        _combatStartSub = bus.Subscribe<CombatStartedEvent>(_ => Schedule());
        _combatEndSub = bus.Subscribe<CombatEndedEvent>(_ =>
        {
            lock (_gate) { _pending.Clear(); }
        });
        _zoneSub = bus.Subscribe<ZoneChangedEvent>(z =>
            _currentZone = string.IsNullOrEmpty(z.ZoneName) ? "Unknown" : z.ZoneName);

        _framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        _combatStartSub.Dispose();
        _combatEndSub.Dispose();
        _zoneSub.Dispose();
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
                var strategy = StrategyPlanResolver.FindMechanicForPrediction(file, prediction);
                var strategyActions = strategy.Profile is not null && strategy.Mechanic is not null
                    ? StrategyPlanResolver.BuildReminderActions(strategy.Profile, strategy.Mechanic)
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
        var hasExplicitArenaView = p.Actions?.Any(a =>
            string.Equals(a.Type, "arena_view", StringComparison.OrdinalIgnoreCase)) == true;
        if (!hasExplicitArenaView)
        {
            TryDrawPredictedMarker(p);
        }

        var actions = p.Actions is null
            ? new List<ActionDefinition> { new() { Type = "tts", Text = $"次: {p.Label}" } }
            : new List<ActionDefinition>(p.Actions);
        if (p.Actions is null)
        {
            actions = new List<ActionDefinition>(BuildDefaultWarningActions(p.Label, p.AdvanceWarningSec));
        }
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

    private void TryDrawPredictedMarker(PendingPrediction p)
    {
        AutoSafeCall? namedSafeCall = null;
        AoeResolver.AoeInfo? aoe = null;
        if (!string.IsNullOrEmpty(p.CastId) &&
            AoeResolver.TryParseCastId(p.CastId, out var actionId))
        {
            namedSafeCall = AutoSafeCallPlanner.CreateKnown(actionId, p.Label);
            aoe = AoeResolver.Resolve(_dataManager, actionId, _log);
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
        var sourceWorld = source is null
            ? (Vector3?)null
            : new Vector3(source.Position.X, source.Position.Y, source.Position.Z);
        var facingAngleRad = ArenaProjection.UsesFacing(visualCall.Gimmick) && source is not null
            ? ArenaProjection.RotationToMapAngleRad(source.Rotation)
            : (float?)null;

        _minimap.AddArenaView(
            gimmick: visualCall.Gimmick,
            callout: $"次: {safeCall.Callout}",
            // 予測通知 → 実発動 → さらに発動後 3 秒残す（「すぐ消えると困る」）
            durationSec: p.AdvanceWarningSec + 5.0 + 3.0,
            direction: ArenaProjection.UsesFacing(visualCall.Gimmick) ? "N" : null,
            fanDeg: visualCall.FanDeg,
            arenaRadius: 20.0,
            directionAngleRad: facingAngleRad,
            sourceWorld: sourceWorld,
            aoeRadius: aoe?.Radius);

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
