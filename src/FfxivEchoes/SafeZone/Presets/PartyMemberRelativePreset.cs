using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.SafeZone.Presets;

/// <summary>
/// PT メンバー基準プリセット（SPEC.md §6.1）。
/// params: { target: target spec（"tank"/"h1"/...）,
///           angle: 角度（0=北、90=東、…）, distance: 数値 }
/// </summary>
public sealed class PartyMemberRelativePreset : ISafeZonePreset
{
    public string Method => "party_member_relative";

    public SafeZoneResult? Calculate(SafeZoneCalculation calc, SafeZoneContext ctx)
    {
        var targetStr = ParamHelper.GetString(calc.Params, "target") ?? "tank";
        var angleDeg = ParamHelper.GetFloat(calc.Params, "angle") ?? 0f;
        var distance = ParamHelper.GetFloat(calc.Params, "distance") ?? 5f;

        IPlayerCharacter? member = ResolveMember(ctx, targetStr);
        if (member is null)
        {
            return null;
        }

        var basePos = new Vector3(member.Position.X, member.Position.Y, member.Position.Z);
        var rad = angleDeg * MathF.PI / 180f;
        var dx = MathF.Sin(rad) * distance;
        var dz = -MathF.Cos(rad) * distance;
        var pos = basePos + new Vector3(dx, 0, dz);

        return new SafeZoneResult(pos, DirectionInfo.Compute(ctx.SelfPosition, pos));
    }

    private static IPlayerCharacter? ResolveMember(SafeZoneContext ctx, string spec)
    {
        // ロール別解決（target_resolver と類似だがここではプレイヤーオブジェクトが必要なため独立）
        var lower = spec.ToLowerInvariant();
        return lower switch
        {
            "tank" => FindByRole(ctx, 1, 0),
            "mt" => FindByRole(ctx, 1, 0),
            "st" => FindByRole(ctx, 1, 1),
            "healer" => FindByRole(ctx, 4, 0),
            "h1" => FindByRole(ctx, 4, 0),
            "h2" => FindByRole(ctx, 4, 1),
            "dps" => FindByRoleOr(ctx, 2, 3, 0),
            "melee" => FindByRole(ctx, 2, 0),
            "ranged" => FindByRole(ctx, 3, 0),
            "caster" => FindByRole(ctx, 3, 0),
            _ => null,
        };
    }

    private static IPlayerCharacter? FindByRole(SafeZoneContext ctx, byte role, int index)
    {
        var n = 0;
        foreach (var p in ctx.Party)
        {
            if (p.ClassJob.Value.Role == role)
            {
                if (n == index)
                {
                    return p;
                }
                n++;
            }
        }
        return null;
    }

    private static IPlayerCharacter? FindByRoleOr(SafeZoneContext ctx, byte role1, byte role2, int index)
    {
        var n = 0;
        foreach (var p in ctx.Party)
        {
            var r = p.ClassJob.Value.Role;
            if (r == role1 || r == role2)
            {
                if (n == index)
                {
                    return p;
                }
                n++;
            }
        }
        return null;
    }
}
