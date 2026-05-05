using System;
using Dalamud.Plugin.Services;
using FfxivEchoes.Capture;
using FfxivEchoes.Events;

namespace FfxivEchoes.Diagnostics;

/// <summary>
/// DebugMode が有効な時、すべての <see cref="IGameEvent"/> をチャットに 1 行で要約出力する。
/// M3 の動作確認用。M4（ロガー）以降は本機能は補助的になる予定。
/// </summary>
public sealed class DebugChatEcho : IDisposable
{
    private readonly Configuration _configuration;
    private readonly IChatGui _chatGui;
    private readonly CombatClock _combatClock;
    private readonly IDisposable _subscription;

    public DebugChatEcho(IEventBus bus, Configuration configuration, IChatGui chatGui, CombatClock combatClock)
    {
        _configuration = configuration;
        _chatGui = chatGui;
        _combatClock = combatClock;

        _subscription = bus.SubscribeAll(OnEvent);
    }

    public void Dispose() => _subscription.Dispose();

    private void OnEvent(IGameEvent ev)
    {
        if (!_configuration.DebugMode)
        {
            return;
        }

        var rel = _combatClock.RelativeSecondsAt(ev.Timestamp);
        var prefix = rel is { } r ? $"[t={r:0.00}s]" : "[非戦闘]";

        var line = ev switch
        {
            CombatStartedEvent => "戦闘開始",
            CombatEndedEvent x => $"戦闘終了（{x.Reason}）",
            ZoneChangedEvent x => $"ゾーン → {x.ZoneName} (#{x.TerritoryId})",
            CastStartedEvent x => $"キャスト開始 {x.SourceName} → {x.CastActionName} ({x.CastTime:0.0}s)",
            CastCompletedEvent x => $"キャスト完了 {x.SourceName} → {x.CastActionName}",
            CastCanceledEvent x => $"キャスト中断 {x.SourceName} → {x.CastActionName}",
            StatusGainedEvent x => $"Status+ {x.TargetName} ← {x.StatusName}"
                + (x.Stacks > 0 ? $" x{x.Stacks}" : string.Empty)
                + (x.RemainingTime > 0 ? $" ({x.RemainingTime:0.0}s)" : string.Empty),
            StatusLostEvent x => $"Status- {x.TargetName} ← {x.StatusName}",
            StatusUpdatedEvent x => $"Status~ {x.StatusId} stacks={x.Stacks} t={x.RemainingTime:0.0}s",
            HpChangedEvent x => $"HP {x.ActorName} {x.HpPct:0.0}% ({x.CurrentHp:N0}/{x.MaxHp:N0})",
            TriggerFiredEvent x => $"⚡ Trigger: {x.TriggerId}{(x.TriggerName is { } n ? $" ({n})" : string.Empty)}",
            _ => $"({ev.GetType().Name})",
        };

        _chatGui.Print($"[Echoes] {prefix} {line}");
    }
}
