using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.SafeZone.Presets;

/// <summary>
/// 線の延長 / 直交（SPEC.md §6.1）。
/// params: { from: actor_spec, to: actor_spec, mode: "extend"|"perpendicular",
///           distance: from→to の長さに対する加算（perpendicular の場合は side との直交距離）,
///           side: "left"|"right"（perpendicular で使用） }
/// </summary>
public sealed class LinePerpendicularPreset : ISafeZonePreset
{
    public string Method => "line_perpendicular";

    private readonly IObjectTable _objectTable;

    public LinePerpendicularPreset(IObjectTable objectTable)
    {
        _objectTable = objectTable;
    }

    public SafeZoneResult? Calculate(SafeZoneCalculation calc, SafeZoneContext ctx)
    {
        var fromSpec = ParamHelper.GetString(calc.Params, "from") ?? "self";
        var toSpec = ParamHelper.GetString(calc.Params, "to") ?? "boss";
        var mode = ParamHelper.GetString(calc.Params, "mode") ?? "extend";
        var distance = ParamHelper.GetFloat(calc.Params, "distance") ?? 5f;
        var side = ParamHelper.GetString(calc.Params, "side") ?? "right";

        var from = ResolveActor(ctx, fromSpec);
        var to = ResolveActor(ctx, toSpec);
        if (from is null || to is null)
        {
            return null;
        }

        var dir = to.Value - from.Value;
        var len = dir.Length();
        if (len < 0.01f)
        {
            return null;
        }
        dir = new Vector3(dir.X / len, 0, dir.Z / len);

        Vector3 result;
        if (mode == "perpendicular")
        {
            result = PerpendicularOffset(from.Value, to.Value, side, distance);
        }
        else
        {
            result = to.Value + dir * distance;
        }

        return new SafeZoneResult(result, DirectionInfo.Compute(ctx.SelfPosition, result));
    }

    /// <summary>
    /// from→to を向いた人の左右（XZ 平面・俯瞰 +X=東 / +Z=南、北=上）に
    /// <paramref name="distance"/> だけ離れた点を返す純粋関数（テスト可能）。
    /// </summary>
    public static Vector3 PerpendicularOffset(Vector3 from, Vector3 to, string side, float distance)
    {
        var raw = to - from;
        var len = raw.Length();
        if (len < 0.01f)
        {
            return to;
        }
        var dir = new Vector3(raw.X / len, 0f, raw.Z / len);
        // from→to を向いた人の左右（俯瞰 +X=東/+Z=南、北=上）。
        // 地上の事実「北を向くと右手は東」から：右=(-z,0,x)、左=(z,0,-x)。
        var normal = string.Equals(side, "left", StringComparison.OrdinalIgnoreCase)
            ? new Vector3(dir.Z, 0f, -dir.X)
            : new Vector3(-dir.Z, 0f, dir.X);
        return to + normal * distance;
    }

    private Vector3? ResolveActor(SafeZoneContext ctx, string spec)
    {
        switch (spec.ToLowerInvariant())
        {
            case "self":
                return ctx.SelfPosition;
            case "boss":
                return ctx.Boss is { } b ? new Vector3(b.Position.X, b.Position.Y, b.Position.Z) : null;
        }
        foreach (var obj in _objectTable)
        {
            if (obj is IBattleChara c && c.Name.TextValue.Contains(spec))
            {
                return new Vector3(c.Position.X, c.Position.Y, c.Position.Z);
            }
        }
        return null;
    }
}
