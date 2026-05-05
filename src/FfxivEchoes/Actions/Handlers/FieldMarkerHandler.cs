using Dalamud.Plugin.Services;
using FfxivEchoes.Events;
using FfxivEchoes.SafeZone;
using FfxivEchoes.Triggers.Models;
using FfxivEchoes.Windows;

namespace FfxivEchoes.Actions.Handlers;

/// <summary>
/// 画面上のワールド座標マーカー（SPEC.md §5.1 field_marker）。
/// ゲーム内のフィールドマーカー（A-H/1-8）操作とは別物：
/// 本実装は「画面上に円や X を描いて指示する」もの。
/// </summary>
public sealed class FieldMarkerHandler : IActionHandler
{
    public string Type => "field_marker";

    private readonly SafeZoneEngine _engine;
    private readonly SafeZoneContextBuilder _contextBuilder;
    private readonly WorldOverlayWindow _worldOverlay;
    private readonly IPluginLog _log;

    public FieldMarkerHandler(
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
        if (action.SafeZone is null)
        {
            _log.Warning("[FfxivEchoes] field_marker に safe_zone 指定がありません");
            return;
        }

        var ctx = _contextBuilder.Build(lastEvent: context.SourceEvent);
        var result = _engine.Calculate(action.SafeZone, ctx);
        if (result is null)
        {
            _log.Debug("[FfxivEchoes] field_marker の SafeZone 解決失敗");
            return;
        }

        var radius = (float)(action.Radius ?? 2.0);
        _worldOverlay.AddMarker(
            worldPos: result.WorldPosition,
            shape: action.Shape ?? "circle",
            radius: radius,
            colorHex: action.Color,
            durationSec: action.Duration ?? 5.0);
    }
}
