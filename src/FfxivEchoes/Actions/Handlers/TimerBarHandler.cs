using FfxivEchoes.Events;
using FfxivEchoes.Triggers.Models;
using FfxivEchoes.Windows;

namespace FfxivEchoes.Actions.Handlers;

/// <summary>
/// タイマーバー表示（SPEC.md §5.1 timer_bar）。
/// </summary>
public sealed class TimerBarHandler : IActionHandler
{
    public string Type => "timer_bar";

    private readonly OverlayWindow _overlay;

    public TimerBarHandler(OverlayWindow overlay)
    {
        _overlay = overlay;
    }

    public void Execute(ActionDefinition action, TriggerFiredEvent context)
    {
        if (action.Duration is not { } duration || duration <= 0)
        {
            return;
        }
        _overlay.AddTimerBar(
            label: action.Label ?? string.Empty,
            durationSec: duration,
            colorHex: action.Color,
            warnAt: action.WarnAt);
    }
}
