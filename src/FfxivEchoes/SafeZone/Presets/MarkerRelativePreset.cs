using System;
using System.Numerics;
using Dalamud.Plugin.Services;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.SafeZone.Presets;

/// <summary>
/// マーカー相対プリセット（SPEC.md §6.1）。
/// params: { origin: "marker_a"〜"marker_h" / "marker_1"〜"marker_8",
///           angle: 度数, distance: 数値 }
/// </summary>
/// <remarks>
/// マーカー位置の取得は F8 の FieldMarkerService 実装後に正しく動作する。
/// 現状は SafeZoneContext.FieldMarkers から参照するだけで、マップ上の位置が
/// 与えられない場合は null を返す。
/// </remarks>
public sealed class MarkerRelativePreset : ISafeZonePreset
{
    public string Method => "marker_relative";

    private readonly IPluginLog _log;

    public MarkerRelativePreset(IPluginLog log)
    {
        _log = log;
    }

    public SafeZoneResult? Calculate(SafeZoneCalculation calc, SafeZoneContext ctx)
    {
        var originStr = ParamHelper.GetString(calc.Params, "origin")?.ToLowerInvariant() ?? "marker_a";
        var angleDeg = ParamHelper.GetFloat(calc.Params, "angle") ?? 0f;
        var distance = ParamHelper.GetFloat(calc.Params, "distance") ?? 0f;

        if (!ctx.FieldMarkers.TryGetValue(originStr, out var basePos))
        {
            _log.Debug("[FfxivEchoes] マーカー '{Marker}' が未配置のため SafeZone 計算をスキップ", originStr);
            return null;
        }

        var rad = angleDeg * MathF.PI / 180f;
        var dx = MathF.Sin(rad) * distance;
        var dz = -MathF.Cos(rad) * distance;
        var pos = basePos + new Vector3(dx, 0, dz);

        return new SafeZoneResult(pos, DirectionInfo.Compute(ctx.SelfPosition, pos));
    }
}
