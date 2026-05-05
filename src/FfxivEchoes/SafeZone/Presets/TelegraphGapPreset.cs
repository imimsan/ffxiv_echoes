using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using FfxivEchoes.SafeZone.Geometry;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.SafeZone.Presets;

/// <summary>
/// AoE 予兆の隙間（SPEC.md §6.1）。複数の telegraph 形状を入力に取り、
/// それらに含まれない位置を返す。
/// params: {
///   search_origin: "self"/"boss"/"arena_center"/[x,y,z]（既定 self）,
///   search_radius: 探索半径（既定 18）,
///   telegraphs: [ shape オブジェクトの配列 ]
/// }
/// </summary>
/// <remarks>
/// 探索アルゴリズム：origin を中心に同心円グリッド（半径方向 4 段、角度方向 24 分割）
/// で点をサンプリングし、最初に「全 telegraph に含まれない」点を返す。
/// 該当点がなければ origin を返す。
/// </remarks>
public sealed class TelegraphGapPreset : ISafeZonePreset
{
    public string Method => "telegraph_gap";

    public SafeZoneResult? Calculate(SafeZoneCalculation calc, SafeZoneContext ctx)
    {
        if (calc.Params is null)
        {
            return null;
        }

        var origin = ResolveOrigin(calc, ctx);
        var searchRadius = ParamHelper.GetFloat(calc.Params, "search_radius") ?? 18f;
        var shapes = BuildShapes(calc.Params, ctx);
        if (shapes.Count == 0)
        {
            return new SafeZoneResult(origin, DirectionInfo.Compute(ctx.SelfPosition, origin));
        }

        // 同心円サンプリング：半径方向に 4 段、角度方向に 24 分割
        const int RingCount = 4;
        const int AngleSlices = 24;

        for (var ring = 1; ring <= RingCount; ring++)
        {
            var r = searchRadius * (ring / (float)RingCount);
            for (var i = 0; i < AngleSlices; i++)
            {
                var angle = i * (2 * MathF.PI / AngleSlices);
                var p = new Vector3(
                    origin.X + MathF.Sin(angle) * r,
                    origin.Y,
                    origin.Z + MathF.Cos(angle) * r);
                if (!IsInAny(p, shapes))
                {
                    return new SafeZoneResult(p, DirectionInfo.Compute(ctx.SelfPosition, p));
                }
            }
        }

        // 全領域が telegraph で覆われている：origin を返す（フォールバック）
        return new SafeZoneResult(origin, DirectionInfo.Compute(ctx.SelfPosition, origin));
    }

    private static Vector3 ResolveOrigin(SafeZoneCalculation calc, SafeZoneContext ctx)
    {
        if (calc.Params is null || !calc.Params.TryGetValue("search_origin", out var v))
        {
            return ctx.SelfPosition;
        }
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString()?.ToLowerInvariant() switch
            {
                "boss" => ctx.Boss is { } b ? new Vector3(b.Position.X, b.Position.Y, b.Position.Z) : ctx.SelfPosition,
                "arena_center" => ctx.ArenaCenter,
                "cast_actor" => ctx.CastActor is { } c ? new Vector3(c.Position.X, c.Position.Y, c.Position.Z) : ctx.SelfPosition,
                _ => ctx.SelfPosition,
            },
            JsonValueKind.Array when v.GetArrayLength() >= 2 =>
                new Vector3(
                    v[0].GetSingle(),
                    v.GetArrayLength() >= 3 ? v[1].GetSingle() : 0f,
                    v.GetArrayLength() >= 3 ? v[2].GetSingle() : v[1].GetSingle()),
            _ => ctx.SelfPosition,
        };
    }

    private static List<TelegraphShape> BuildShapes(IReadOnlyDictionary<string, JsonElement> p, SafeZoneContext ctx)
    {
        var list = new List<TelegraphShape>();
        if (!p.TryGetValue("telegraphs", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return list;
        }
        foreach (var el in arr.EnumerateArray())
        {
            var shape = TelegraphShape.FromJson(el, ctx);
            if (shape is not null)
            {
                list.Add(shape);
            }
        }
        return list;
    }

    private static bool IsInAny(Vector3 p, List<TelegraphShape> shapes)
    {
        foreach (var s in shapes)
        {
            if (s.Contains(p))
            {
                return true;
            }
        }
        return false;
    }
}
