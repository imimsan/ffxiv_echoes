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
        // telegraph を指定するキーが存在するか（"telegraphs" 標準 / "actors" は未対応キー）。
        var hasTelegraphKey = calc.Params.ContainsKey("telegraphs") || calc.Params.ContainsKey("actors");
        var shapes = BuildShapes(calc.Params, ctx);
        if (shapes.Count == 0)
        {
            // telegraph を期待するキーがあるのに 0 件 = 解釈失敗（例: 未対応の "actors" キー）。
            // self 位置を「安置」と誤提示すると AoE 内へ誘導しかねないため null を返す（呼び出し側はガード済み）。
            // キー自体が無い＝避ける対象が無い場合のみ origin（その場待機）を返す。
            return hasTelegraphKey
                ? null
                : new SafeZoneResult(origin, DirectionInfo.Compute(ctx.SelfPosition, origin));
        }

        // クリアランス（隙間の縁ギリギリでなく、この距離だけ全方位の余裕を確保した点を優先）。
        // 既定 1.0m（プレイヤー hitbox 半径 + α 相当）。0 以下なら従来どおりマージン無し。
        var clearanceMargin = ParamHelper.GetFloat(calc.Params, "clearance") ?? 1.0f;
        var safe = FindSafePoint(origin, searchRadius, shapes, clearanceMargin);

        // 安全点ゼロ（全領域 telegraph）：self を「安置」と誤提示しないため null。
        // 呼び出し側（DirectionCall/FieldMarker/ProximityFeedback）は result null をガード済み。
        return safe is { } p
            ? new SafeZoneResult(p, DirectionInfo.Compute(ctx.SelfPosition, p))
            : null;
    }

    /// <summary>
    /// origin 中心の同心円グリッドを走査し、どの telegraph にも含まれない「安置」点を返す。
    /// <paramref name="clearanceMargin"/> だけ全方位の余裕がある点を優先し、見つからなければ
    /// 素の安全点（真の激狭安置）を返す。安全点が皆無なら null。形状の内外判定は
    /// <see cref="TelegraphShape.Contains"/> の再利用のみで、座標符号の独自計算はしない。
    /// </summary>
    public static Vector3? FindSafePoint(
        Vector3 origin, float searchRadius, IReadOnlyList<TelegraphShape> shapes, float clearanceMargin)
    {
        const int RingCount = 6;
        const int AngleSlices = 72;   // 5°粒度
        if (searchRadius <= 0f)
        {
            searchRadius = 1f;
        }

        Vector3? bareSafe = null;

        // origin（その場待機）を最優先候補にする：既に安全なら不要な移動を避ける。
        if (!IsInAny(origin, shapes))
        {
            bareSafe = origin;
            if (HasClearance(origin, shapes, clearanceMargin))
            {
                return origin;
            }
        }

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
                if (IsInAny(p, shapes))
                {
                    continue;
                }
                bareSafe ??= p;                       // マージン無しフォールバック（最初の安全点）
                if (HasClearance(p, shapes, clearanceMargin))
                {
                    return p;                         // マージン確保点を優先（縁ギリギリを避ける）
                }
            }
        }

        return bareSafe;                              // マージン点が無ければ素の安全点（真の激狭）
    }

    /// <summary>点 p の周囲 <paramref name="margin"/> に telegraph が無い（全方位に余裕がある）か。</summary>
    private static bool HasClearance(Vector3 p, IReadOnlyList<TelegraphShape> shapes, float margin)
    {
        if (margin <= 0f)
        {
            return true;
        }
        const int Probes = 8;
        for (var k = 0; k < Probes; k++)
        {
            var a = k * (2 * MathF.PI / Probes);
            var probe = new Vector3(
                p.X + MathF.Sin(a) * margin,
                p.Y,
                p.Z + MathF.Cos(a) * margin);
            if (IsInAny(probe, shapes))
            {
                return false;
            }
        }
        return true;
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

    private static bool IsInAny(Vector3 p, IReadOnlyList<TelegraphShape> shapes)
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
