using System;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;
using FfxivEchoes.Triggers.Matching;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Triggers;

/// <summary>
/// イベントバスを購読し、ロード済みのトリガー定義とマッチさせて
/// <see cref="TriggerFiredEvent"/> を発行する。
/// </summary>
public sealed class TriggerEngine : IDisposable
{
    private readonly IEventBus _bus;
    private readonly TriggerStore _store;
    private readonly EventMatcher _matcher;
    private readonly CooldownTracker _cooldowns;
    private readonly IPluginLog _log;
    private readonly IDisposable _allEventsSub;

    private string _currentZone = "Unknown";

    public TriggerEngine(IEventBus bus, TriggerStore store, EventMatcher matcher, IPluginLog log)
    {
        _bus = bus;
        _store = store;
        _matcher = matcher;
        _log = log;
        _cooldowns = new CooldownTracker();

        _allEventsSub = bus.SubscribeAll(OnEvent);
    }

    public void Dispose()
    {
        _allEventsSub.Dispose();
        _cooldowns.Reset();
    }

    private void OnEvent(IGameEvent ev)
    {
        // TriggerFired を再帰購読しないように除外（ループ防止）
        if (ev is TriggerFiredEvent)
        {
            return;
        }

        // Zone state のメンテナンス
        if (ev is ZoneChangedEvent zce)
        {
            _currentZone = string.IsNullOrEmpty(zce.ZoneName) ? "Unknown" : zce.ZoneName;
            _cooldowns.Reset();
        }
        if (ev is CombatEndedEvent)
        {
            _cooldowns.Reset();
        }

        var triggerFile = _store.GetByZone(_currentZone);
        if (triggerFile is null)
        {
            return;
        }
        if (!triggerFile.AutoSettings.EnableTriggers)
        {
            return;
        }

        foreach (var trigger in triggerFile.Triggers)
        {
            if (!trigger.Enabled)
            {
                continue;
            }
            if (!_matcher.Matches(trigger, ev))
            {
                continue;
            }
            if (_cooldowns.IsOnCooldown(trigger.Id, trigger.Cooldown ?? 0, ev.Timestamp))
            {
                continue;
            }

            _cooldowns.MarkFired(trigger.Id, ev.Timestamp);
            try
            {
                _bus.Publish(new TriggerFiredEvent(
                    Timestamp: ev.Timestamp,
                    Zone: _currentZone,
                    TriggerId: trigger.Id,
                    TriggerName: trigger.Name,
                    Actions: trigger.Actions,
                    SourceEvent: ev));
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[FfxivEchoes] TriggerFiredEvent の発行で例外（trigger={Id}）", trigger.Id);
            }
        }
    }
}
