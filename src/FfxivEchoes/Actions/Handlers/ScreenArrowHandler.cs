using Dalamud.Plugin.Services;
using FfxivEchoes.Events;
using FfxivEchoes.SafeZone;
using FfxivEchoes.Triggers.Models;
using FfxivEchoes.Windows;

namespace FfxivEchoes.Actions.Handlers;

public sealed class ScreenArrowHandler : IActionHandler
{
    public string Type => "screen_arrow";

    private readonly SafeZoneEngine _engine;
    private readonly SafeZoneContextBuilder _contextBuilder;
    private readonly WorldOverlayWindow _worldOverlay;
    private readonly IPluginLog _log;

    public ScreenArrowHandler(
        SafeZoneEngine engine, SafeZoneContextBuilder contextBuilder,
        WorldOverlayWindow worldOverlay, IPluginLog log)
    {
        _engine = engine;
        _contextBuilder = contextBuilder;
        _worldOverlay = worldOverlay;
        _log = log;
    }

    public void Execute(ActionDefinition action, TriggerFiredEvent context)
    {
        // To が SafeZone なら計算、文字列なら "self" / 名前を解釈
        var ctx = _contextBuilder.Build(lastEvent: context.SourceEvent);

        // SafeZone-driven target
        if (action.SafeZone is { } sz)
        {
            var result = _engine.Calculate(sz, ctx);
            if (result is null)
            {
                _log.Debug("[FfxivEchoes] screen_arrow の SafeZone 解決失敗");
                return;
            }
            var fromSelf = string.IsNullOrEmpty(action.From) || action.From.Equals("self", System.StringComparison.OrdinalIgnoreCase);
            _worldOverlay.AddArrow(
                fromSelf: fromSelf,
                from: null,
                to: result.WorldPosition,
                colorHex: action.Color,
                durationSec: action.Duration ?? 5.0);
            return;
        }

        _log.Warning("[FfxivEchoes] screen_arrow に safe_zone 指定がありません");
    }
}
