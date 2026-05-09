using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Triggers;

public sealed class AutoAttackTimingService : IDisposable
{
    private const double WarningBeforeSeconds = 1.2;

    private readonly IFramework _framework;
    private readonly IEventBus _bus;
    private readonly TriggerStore _store;
    private readonly IObjectTable _objectTable;
    private readonly IPluginLog _log;
    private readonly AutoAttackCadenceTracker _tracker = new();

    private readonly IDisposable _actionSub;
    private readonly IDisposable _combatEndSub;
    private readonly IDisposable _zoneSub;

    private string _currentZone = "Unknown";

    public AutoAttackTimingService(
        IFramework framework,
        IEventBus bus,
        TriggerStore store,
        IObjectTable objectTable,
        IPluginLog log)
    {
        _framework = framework;
        _bus = bus;
        _store = store;
        _objectTable = objectTable;
        _log = log;

        _actionSub = bus.Subscribe<ActionUsedEvent>(OnActionUsed);
        _combatEndSub = bus.Subscribe<CombatEndedEvent>(_ => _tracker.Clear());
        _zoneSub = bus.Subscribe<ZoneChangedEvent>(z =>
        {
            _currentZone = string.IsNullOrEmpty(z.ZoneName) ? "Unknown" : z.ZoneName;
            _tracker.Clear();
        });

        _framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        _actionSub.Dispose();
        _combatEndSub.Dispose();
        _zoneSub.Dispose();
        _framework.Update -= OnUpdate;
        _tracker.Clear();
    }

    private void OnActionUsed(ActionUsedEvent ev)
    {
        if (!ev.IsAutoAttack || !ShouldTrackAutoAttacks())
        {
            return;
        }

        if (IsFriendlyActor(ev.SourceId))
        {
            return;
        }

        var prediction = _tracker.Record(new AutoAttackSample(ev.SourceId, ev.TargetId, ev.Timestamp));
        if (prediction is null)
        {
            return;
        }

        PublishTimer(ev, prediction);
    }

    private void OnUpdate(IFramework _)
    {
        if (!ShouldTrackAutoAttacks())
        {
            return;
        }

        var warnings = _tracker.GetDueWarnings(DateTimeOffset.UtcNow, WarningBeforeSeconds);
        if (warnings.Count == 0)
        {
            return;
        }

        var text = warnings.Count == 1 ? "AA" : $"AA x{warnings.Count}";
        var first = warnings[0];
        _bus.Publish(new TriggerFiredEvent(
            Timestamp: DateTimeOffset.UtcNow,
            Zone: _currentZone,
            TriggerId: $"__auto_attack_warning_{first.SourceId}_{first.ExpectedAt:O}",
            TriggerName: text,
            Actions: new List<ActionDefinition>
            {
                new()
                {
                    Type = "tts",
                    Text = text,
                },
                new()
                {
                    Type = "overlay_text",
                    Text = text,
                    Duration = 0.9,
                    Color = "#FBBF24",
                    Size = "large",
                },
            },
            SourceEvent: new ActionUsedEvent(
                DateTimeOffset.UtcNow,
                first.SourceId,
                "AA prediction",
                0,
                "AA prediction",
                first.TargetId,
                true)));
    }

    private void PublishTimer(ActionUsedEvent ev, AutoAttackPrediction prediction)
    {
        var label = string.IsNullOrWhiteSpace(ev.SourceName)
            ? "AA"
            : $"AA: {TrimLabel(ev.SourceName, 14)}";

        _bus.Publish(new TriggerFiredEvent(
            Timestamp: DateTimeOffset.UtcNow,
            Zone: _currentZone,
            TriggerId: $"__auto_attack_timer_{ev.SourceId}_{ev.TargetId ?? 0}",
            TriggerName: label,
            Actions: new List<ActionDefinition>
            {
                new()
                {
                    Type = "timer_bar",
                    Label = label,
                    Duration = prediction.IntervalSeconds,
                    Color = "#FBBF24",
                    WarnAt = WarningBeforeSeconds,
                },
            },
            SourceEvent: ev));

        _log.Debug("[FfxivEchoes] AA timer: source={Source} interval={Interval:0.00}s confidence={Confidence:0.00}",
            ev.SourceName, prediction.IntervalSeconds, prediction.Confidence);
    }

    private bool ShouldTrackAutoAttacks()
    {
        var file = _store.GetByZone(_currentZone);
        return file?.AutoSettings.ShowAutoAttacks == true;
    }

    private bool IsFriendlyActor(uint id)
    {
        var obj = _objectTable.SearchById(id);
        return obj?.ObjectKind == ObjectKind.Pc;
    }

    private static string TrimLabel(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }
}
