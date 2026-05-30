using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FfxivEchoes.Capture;
using FfxivEchoes.Events;
using FfxivEchoes.Recording;
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
    private readonly RecordingScanner _recordings;
    private readonly SyncOffsetTracker _syncOffset;
    private readonly IPluginLog _log;
    private readonly Func<string?, bool>? _branchActiveCheck;
    private readonly Func<string?, bool>? _phaseActiveCheck;

    private readonly IDisposable _combatStartSub;
    private readonly IDisposable _combatEndSub;
    private readonly IDisposable _zoneSub;
    private readonly IDisposable _branchResolvedSub;
    private readonly IDisposable _phaseTransitionSub;

    private readonly List<PendingNote> _pending = new();
    private readonly object _gate = new();
    private string _currentZone = "Unknown";

    public NoteReminderService(
        IFramework framework, IEventBus bus, TriggerStore store, CombatClock combatClock,
        IPlayerState playerState, RecordingScanner recordings, SyncOffsetTracker syncOffset, IPluginLog log,
        Func<string?, bool>? branchActiveCheck = null,
        Func<string?, bool>? phaseActiveCheck = null)
    {
        _framework = framework;
        _bus = bus;
        _store = store;
        _combatClock = combatClock;
        _playerState = playerState;
        _recordings = recordings;
        _syncOffset = syncOffset;
        _log = log;
        _branchActiveCheck = branchActiveCheck;
        _phaseActiveCheck = phaseActiveCheck;

        _combatStartSub = bus.Subscribe<CombatStartedEvent>(_ => Schedule());
        _combatEndSub = bus.Subscribe<CombatEndedEvent>(_ =>
        {
            lock (_gate) { _pending.Clear(); }
        });
        _zoneSub = bus.Subscribe<ZoneChangedEvent>(z =>
            _currentZone = string.IsNullOrEmpty(z.ZoneName) ? "Unknown" : z.ZoneName);
        // 分岐確定後に branch_id 付き mechanic を再スケジュールする。これが無いと、CombatStart 時点では
        // 未確定（IsBranchAllowed=false）だった分岐固有ギミックの先行通知が永久に発火しない。
        // PredictedCastReminderService と同じ BranchResolvedEvent 購読パターン。
        _branchResolvedSub = bus.Subscribe<BranchResolvedEvent>(_ => Schedule());
        // フェーズ遷移後に再スケジュール。過去フェーズの mechanic は _phaseActiveCheck で除外され、
        // 既発火ノートの再発火は OnUpdate の staleガード（ShouldFireDueNote）が防ぐ。
        _phaseTransitionSub = bus.Subscribe<PhaseTransitionedEvent>(_ => Schedule());
        // 戦闘中にトリガー JSON を編集・保存（TriggerWatcher 経由 Reload）したら再スケジュールする。
        // これが無いと追加したノートは通知されず、削除したノートは発火し続ける。
        // Reloaded は TriggerWatcher のデバウンスタイマー（ワーカースレッド）から発火するため、
        // _currentZone の無保護読み・ロック競合を避けてフレームスレッドにマーシャリングする。
        _store.Reloaded += OnTriggerStoreReloaded;

        _framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        _combatStartSub.Dispose();
        _combatEndSub.Dispose();
        _zoneSub.Dispose();
        _branchResolvedSub.Dispose();
        _phaseTransitionSub.Dispose();
        _store.Reloaded -= OnTriggerStoreReloaded;
        _framework.Update -= OnUpdate;
        lock (_gate) { _pending.Clear(); }
    }

    private void OnTriggerStoreReloaded()
        => _framework.RunOnFrameworkThread(() =>
        {
            try { Schedule(); }
            catch (Exception ex) { _log.Error(ex, "[FfxivEchoes] NoteReminder: Reload 後の再スケジュール失敗"); }
        });

    private void Schedule()
    {
        var file = _store.GetByZone(_currentZone);
        if (file is null)
        {
            return;
        }

        // AttachedTo 解決のため、現ゾーンの録画 aggregate を一度だけ取得
        Recording.AggregatedEvents? agg = null;
        try
        {
            agg = _recordings.Aggregate(_currentZone);
        }
        catch (Exception ex)
        {
            // 黙殺すると AttachedTo 付きノートが無音になり原因特定できないためログを残す（agg=null で続行）。
            _log.Warning(ex, "[FfxivEchoes] NoteReminder: 録画 aggregate 失敗 zone={Zone}", _currentZone);
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
                var resolved = TimelineNoteResolver.ResolveTime(note, agg);
                if (resolved is null) continue;
                var fireTime = resolved.Value - warn;
                if (fireTime < 0)
                {
                    fireTime = 0;
                }
                _pending.Add(new PendingNote(note.Id, fireTime, resolved.Value, note));
            }

            var strategyProfile = StrategyPlanResolver.SelectActiveProfile(file);
            if (strategyProfile is not null)
            {
                foreach (var mechanic in strategyProfile.Mechanics)
                {
                    if (!mechanic.Enabled ||
                        !IsBranchAllowed(mechanic) ||
                        !IsPhaseAllowed(mechanic) ||
                        mechanic.AdvanceWarningSec is not { } warn ||
                        warn <= 0)
                    {
                        continue;
                    }

                    var note = StrategyPlanResolver.BuildTimelineNote(strategyProfile, mechanic);
                    if (!MatchesPlayer(note))
                    {
                        continue;
                    }

                    var resolved = TimelineNoteResolver.ResolveTime(note, agg);
                    if (resolved is null)
                    {
                        continue;
                    }

                    var fireTime = resolved.Value - warn;
                    if (fireTime < 0)
                    {
                        fireTime = 0;
                    }

                    _pending.Add(new PendingNote(
                        note.Id,
                        fireTime,
                        resolved.Value,
                        note,
                        StrategyPlanResolver.BuildReminderActions(file, strategyProfile, mechanic)));
                }
            }
        }
        _log.Debug("[FfxivEchoes] NoteReminder: {Count} 件をスケジュール", _pending.Count);
    }

    private bool IsBranchAllowed(MechanicStrategy mechanic)
        => _branchActiveCheck?.Invoke(mechanic.BranchId) ?? true;

    private bool IsPhaseAllowed(MechanicStrategy mechanic)
        => _phaseActiveCheck?.Invoke(mechanic.Phase) ?? true;

    /// <summary>
    /// 「発火予定時刻が来たノート」を実際に発火すべきか判定する staleガード。
    /// Schedule() が状態変化（分岐確定・フェーズ遷移・戦闘開始）のたびに _pending を
    /// 全件再構築するため、実イベント時刻が既に過去のノートは fireTime=0 にクランプされて
    /// 即時発火してしまう。これを <see cref="PredictedCastReminderService.ShouldFireDuePrediction"/>
    /// と同一判定で防ぐ（重複読み上げの根本対策）。
    /// </summary>
    public static bool ShouldFireDueNote(
        double fireAtRelSec,
        double eventAtRelSec,
        double offsetSec,
        double nowRelSec,
        double staleGraceSec = 1.0)
        => PredictedCastReminderService.ShouldFireDuePrediction(
            fireAtRelSec, eventAtRelSec, offsetSec, nowRelSec, staleGraceSec);

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
        // 同期オフセットを適用：実時刻が「予測時刻 + offset」に達したら発火
        var offset = _syncOffset.CurrentOffsetSec;

        List<PendingNote>? toFire = null;
        lock (_gate)
        {
            for (var i = _pending.Count - 1; i >= 0; i--)
            {
                if (_pending[i].FireAtRelSec + offset <= nowRel.Value)
                {
                    // 再スケジュール（BranchResolved / PhaseTransitioned）で過去ノートが
                    // _pending に再投入されても、実イベント時刻が現在より前なら発火しない。
                    // PredictedCastReminderService と同じ staleガード。
                    if (ShouldFireDueNote(
                            _pending[i].FireAtRelSec,
                            _pending[i].EventAtRelSec,
                            offset,
                            nowRel.Value))
                    {
                        toFire ??= new List<PendingNote>();
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
                FireNote(p);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[FfxivEchoes] NoteReminder 通知失敗：{Id}", p.NoteId);
            }
        }
    }

    private void FireNote(PendingNote pending)
    {
        var note = pending.Note;
        var text = string.IsNullOrEmpty(note.WarningText) ? note.Label : note.WarningText;
        var customActions = pending.Actions?.ToList();
        if (customActions is null && string.IsNullOrEmpty(text))
        {
            return;
        }

        // ノートの先行通知も音声のみ。中央オーバーレイは画面が埋まるため出さない。
        // 視覚通知は LiveTimeline / UpcomingEventsWindow / 「📌 ノート」表示で見える。
        var actions = new List<Models.ActionDefinition>
        {
            new() { Type = "tts", Text = text },
        };
        if (customActions is not null)
        {
            actions = customActions;
        }
        var file = _store.GetByZone(_currentZone);
        actions = AutoSafeCallPlanner
            .RemoveMinimapActionsForRaidWideMatch(file, actions, note.AttachedTo)
            .ToList();
        if (actions.Count == 0)
        {
            return;
        }
        _bus.Publish(new TriggerFiredEvent(
            Timestamp: DateTimeOffset.UtcNow,
            Zone: _currentZone,
            TriggerId: $"__timeline_note_{note.Id}",
            TriggerName: note.Label,
            Actions: actions,
            SourceEvent: new CombatStartedEvent(DateTimeOffset.UtcNow)));
    }

    private readonly record struct PendingNote(
        string NoteId,
        double FireAtRelSec,
        double EventAtRelSec,
        TimelineNote Note,
        IReadOnlyList<Models.ActionDefinition>? Actions = null);
}
