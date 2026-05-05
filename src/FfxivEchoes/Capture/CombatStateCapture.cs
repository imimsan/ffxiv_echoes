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
