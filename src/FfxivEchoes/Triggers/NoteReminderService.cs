using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FfxivEchoes.Capture;
using FfxivEchoes.Events;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Triggers;

/// <summary>
/// TimelineNote の advance_warning_sec を監視して、時刻が来たら TTS / オーバーレイで
/// 先行通知する。CombatStarted で対象のノートをスケジュール、CombatEnded でクリア。
/// </summary>
public sealed class NoteReminderService : IDisposable
{
    private readonly IFramework _framework;
    private readonly IEventBus _bus;
    private readonly TriggerStore _store;
    private readonly CombatClock _combatClock;
    private readonly IPlayerState _playerState;
    private readonly IPluginLog _log;

    private readonly IDisposable _combatStartSub;
    private readonly IDisposable _combatEndSub;
    private readonly IDisposable _zoneSub;

    private readonly List<PendingNote> _pending = new();
    private readonly object _gate = new();
    private string _currentZone = "Unknown";

    public NoteReminderService(
        IFramework framework, IEventBus bus, TriggerStore store, CombatClock combatClock,
        IPlayerState playerState, IPluginLog log)
    {
        _framework = framework;
        _bus = bus;
        _store = store;
        _combatClock = combatClock;
        _playerState = playerState;
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

        lock (_gate)
        {
            _pending.Clear();
            foreach (var note in file.Notes)
            {
                if (note.AdvanceWarningSec is not { } warn || warn <= 0)
                {
                    continue;
                }
                if (!MatchesPlayer(note))
                {
                    continue;
                }
                var fireTime = note.Time - warn;
                if (fireTime < 0)
                {
                    fireTime = 0;
                }
                _pending.Add(new PendingNote(note.Id, fireTime, note));
            }
        }
        _log.Debug("[FfxivEchoes] NoteReminder: {Count} 件をスケジュール", _pending.Count);
    }

    private bool MatchesPlayer(TimelineNote note)
    {
        // role 指定があり、自分のロールと一致するかチェック
        if (!string.IsNullOrEmpty(note.Role) && _playerState.IsLoaded)
        {
            var role = _playerState.ClassJob.Value.Role;
            var matches = note.Role.ToLowerInvariant() switch
            {
                "any" => true,
                "tank" or "mt" or "st" => role == 1,
                "healer" or "h1" or "h2" => role == 4,
                "dps" => role == 2 || role == 3,
                "melee" => role == 2,
                "ranged" or "caster" => role == 3,
                _ => true, // 未知のロール指定は全員対象
            };
            if (!matches)
            {
                return false;
            }
        }
        // job 指定があり、自分のジョブ略称と一致するかチェック
        if (!string.IsNullOrEmpty(note.Job) && _playerState.IsLoaded)
        {
            var myJob = _playerState.ClassJob.Value.Abbreviation.ToString();
            if (!string.Equals(myJob, note.Job, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    private void OnUpdate(IFramework _)
    {
        var nowRel = _combatClock.RelativeSecondsAt(DateTimeOffset.UtcNow);
        if (nowRel is null)
        {
            return;
        }

        List<PendingNote>? toFire = null;
        lock (_gate)
        {
            for (var i = _pending.Count - 1; i >= 0; i--)
            {
                if (_pending[i].FireAtRelSec <= nowRel.Value)
                {
                    toFire ??= new List<PendingNote>();
                    toFire.Add(_pending[i]);
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
                FireNote(p.Note);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[FfxivEchoes] NoteReminder 通知失敗：{Id}", p.NoteId);
            }
        }
    }

    private void FireNote(TimelineNote note)
    {
        var text = string.IsNullOrEmpty(note.WarningText) ? note.Label : note.WarningText;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var actions = new List<Models.ActionDefinition>
        {
            new() { Type = "tts", Text = text },
            new() { Type = "overlay_text", Text = text, Duration = 4.0, Color = note.Color },
        };
        _bus.Publish(new TriggerFiredEvent(
            Timestamp: DateTimeOffset.UtcNow,
            Zone: _currentZone,
            TriggerId: $"__timeline_note_{note.Id}",
            TriggerName: note.Label,
            Actions: actions,
            SourceEvent: new CombatStartedEvent(DateTimeOffset.UtcNow)));
    }

    private readonly record struct PendingNote(string NoteId, double FireAtRelSec, TimelineNote Note);
}
