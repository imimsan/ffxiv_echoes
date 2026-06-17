using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;

namespace FfxivEchoes.Capture;

/// <summary>
/// <see cref="ICondition"/> を監視して <see cref="CombatStartedEvent"/> /
/// <see cref="CombatEndedEvent"/> を発行する。
/// </summary>
/// <remarks>
/// クリア／ワイプの判定は本キャプチャでは行わない（Reason.Unknown を発行）。
/// 詳細な判定は将来 ContentDirector や ActionEffect 監視で補完する想定。
/// </remarks>
public sealed class CombatStateCapture : IDisposable
{
    private readonly ICondition _condition;
    private readonly IEventBus _bus;
    private readonly IPluginLog _log;
    private bool _wasInCombat;

    public CombatStateCapture(ICondition condition, IEventBus bus, IPluginLog log)
    {
        _condition = condition;
        _bus = bus;
        _log = log;

        _wasInCombat = condition[ConditionFlag.InCombat];
        _condition.ConditionChange += OnConditionChange;
    }

    public void Dispose()
    {
        _condition.ConditionChange -= OnConditionChange;
    }

    /// <summary>
    /// プラグイン読み込み時に既に戦闘中の場合のため、現在の戦闘状態を一度だけ
    /// <see cref="CombatStartedEvent"/> として発行する。すべての subscriber が attach
    /// された後に Plugin.cs から呼ぶこと（<see cref="ZoneCapture.PublishInitialState"/> と同型）。
    /// これが無いと、戦闘中にプラグインをロード／Reload した pull では CombatStartedEvent が
    /// 一度も出ず、CombatClock が始動せずに RelativeSecondsAt が null のままになり、予測 TTS・
    /// mechanic・同期オフセットがその 1 戦だけ全停止する。
    /// </summary>
    public void PublishInitialState()
    {
        if (!_condition[ConditionFlag.InCombat])
        {
            return;
        }
        _wasInCombat = true;
        _log.Information("[FfxivEchoes] 初期状態：既に戦闘中 → CombatStarted を発行");
        _bus.Publish(new CombatStartedEvent(DateTimeOffset.UtcNow));
    }

    private void OnConditionChange(ConditionFlag flag, bool value)
    {
        if (flag != ConditionFlag.InCombat)
        {
            return;
        }
        if (value == _wasInCombat)
        {
            return;
        }

        _wasInCombat = value;
        var now = DateTimeOffset.UtcNow;
        if (value)
        {
            _log.Debug("[FfxivEchoes] CombatStarted");
            _bus.Publish(new CombatStartedEvent(now));
        }
        else
        {
            _log.Debug("[FfxivEchoes] CombatEnded");
            _bus.Publish(new CombatEndedEvent(now, CombatEndReason.Unknown));
        }
    }
}
