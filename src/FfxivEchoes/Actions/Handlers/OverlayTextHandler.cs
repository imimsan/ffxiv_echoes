using FfxivEchoes.Events;
using FfxivEchoes.Triggers.Models;
using FfxivEchoes.Windows;

namespace FfxivEchoes.Actions.Handlers;

/// <summary>
/// 画面中央への大型テキスト表示（SPEC.md §5.1 overlay_text）。
/// </summary>
public sealed class OverlayTextHandler : IActionHandler
{
    public string Type => "overlay_text";

    private readonly OverlayWindow _overlay;

    public OverlayTextHandler(OverlayWindow overlay)
    {
        _overlay = overlay;
    }

    public void Execute(ActionDefinition action, TriggerFiredEvent context)
    {
        if (string.IsNullOrEmpty(action.Text))
        {
            return;
        }
        _overlay.AddText(
            text: action.Text,
            durationSec: action.Duration ?? 5.0,
            colorHex: action.Color,
            size: action.Size);
    }
}
