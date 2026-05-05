using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.SafeZone.Presets;

/// <summary>
/// 2 アクター間の比率位置（SPEC.md §6.1）。
/// params: { from: actor_spec, to: actor_spec, ratio: 0.0〜1.0（既定 0.5） }
/// 例：ノックバック中間地点 = ratio 0.5。
/// </summary>
public sealed class BetweenActorsPreset : ISafeZonePreset
{
    public string Method => "between_actors";

    private readonly IObjectTable _objectTable;

    public BetweenActorsPreset(IObjectTable objectTable)
    {
        _objectTable = objectTable;
    }

    public SafeZoneResult? Calculate(SafeZoneCalculation calc, SafeZoneContext ctx)
    {
        var fromSpec = ParamHelper.GetString(calc.Params, "from") ?? "self";
        var toSpec = ParamHelper.GetString(calc.Params, "to") ?? "boss";
        var ratio = ParamHelper.GetFloat(calc.Params, "ratio") ?? 0.5f;

        var from = ResolveActor(ctx, fromSpec);
        var to = ResolveActor(ctx, toSpec);
        if (from is null || to is null)
        {
            return null;
        }

        var pos = Vector3.Lerp(from.Value, to.Value, ratio);
        return new SafeZoneResult(pos, DirectionInfo.Compute(ctx.SelfPosition, pos));
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
