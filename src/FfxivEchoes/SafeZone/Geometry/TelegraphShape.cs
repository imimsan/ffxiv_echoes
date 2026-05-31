using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;

namespace FfxivEchoes.SafeZone.Geometry;

/// <summary>
/// AoE 予兆の形状。XZ 平面上の判定のみ（高さは無視）。
/// </summary>
public abstract class TelegraphShape
{
    /// <summary>点 <paramref name="p"/> が形状内（範囲内）かを返す。</summary>
    public abstract bool Contains(Vector3 p);

    /// <summary>JSON オブジェクトから形状を構築する。"shape" フィールドで分岐。</summary>
    public static TelegraphShape? FromJson(JsonElement el, SafeZoneContext ctx)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty("shape", out var sp))
        {
            return null;
        }
        var shape = sp.GetString()?.ToLowerInvariant() ?? string.Empty;
        return shape switch
        {
            "circle" => CircleShape.FromJson(el, ctx),
            "fan" => FanShape.FromJson(el, ctx),
            "line" => LineShape.FromJson(el, ctx),
            "donut" => DonutShape.FromJson(el, ctx),
            "rect" or "rectangle" => RectShape.FromJson(el, ctx),
            _ => null,
        };
    }

    protected static Vector3? ResolvePosition(JsonElement el, string key, SafeZoneContext ctx)
    {
        if (!el.TryGetProperty(key, out var v))
        {
            return null;
        }
        return v.ValueKind switch
        {
            JsonValueKind.String => ResolveByName(v.GetString() ?? string.Empty, ctx),
            JsonValueKind.Array => ResolveByArray(v),
            JsonValueKind.Object => ResolveByObject(v),
            _ => null,
        };
    }

    private static Vector3? ResolveByName(string name, SafeZoneContext ctx)
    {
        return name.ToLowerInvariant() switch
        {
            "self" => ctx.SelfPosition,
            "boss" => ctx.Boss is { } b ? new Vector3(b.Position.X, b.Position.Y, b.Position.Z) : null,
            "arena_center" => ctx.ArenaCenter,
            "cast_actor" => ctx.CastActor is { } c ? new Vector3(c.Position.X, c.Position.Y, c.Position.Z) : null,
            _ => null,
        };
    }

    private static Vector3? ResolveByArray(JsonElement arr)
    {
        if (arr.GetArrayLength() < 2)
        {
            return null;
        }
        var x = arr[0].GetSingle();
        var y = arr.GetArrayLength() >= 3 ? arr[1].GetSingle() : 0f;
        var z = arr.GetArrayLength() >= 3 ? arr[2].GetSingle() : arr[1].GetSingle();
        return new Vector3(x, y, z);
    }

    private static Vector3? ResolveByObject(JsonElement obj)
    {
        var x = obj.TryGetProperty("x", out var xp) ? xp.GetSingle() : 0f;
        var y = obj.TryGetProperty("y", out var yp) ? yp.GetSingle() : 0f;
        var z = obj.TryGetProperty("z", out var zp) ? zp.GetSingle() : 0f;
        return new Vector3(x, y, z);
    }

    protected static float GetFloat(JsonElement el, string key, float fallback)
    {
        return el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetSingle()
            : fallback;
    }
}

public sealed class CircleShape : TelegraphShape
{
    public Vector3 Center { get; init; }
    public float Radius { get; init; }

    public override bool Contains(Vector3 p)
    {
        var dx = p.X - Center.X;
        var dz = p.Z - Center.Z;
        return dx * dx + dz * dz <= Radius * Radius;
    }

    public new static CircleShape? FromJson(JsonElement el, SafeZoneContext ctx)
    {
        var center = ResolvePosition(el, "center", ctx);
        if (center is null)
        {
            return null;
        }
        return new CircleShape { Center = center.Value, Radius = GetFloat(el, "radius", 5f) };
    }
}

public sealed class FanShape : TelegraphShape
{
    public Vector3 Origin { get; init; }
    public float FacingDeg { get; init; }
    public float AngleWidthDeg { get; init; }
    public float Radius { get; init; }

    public override bool Contains(Vector3 p)
    {
        var dx = p.X - Origin.X;
        var dz = p.Z - Origin.Z;
        var distSq = dx * dx + dz * dz;
        if (distSq > Radius * Radius)
        {
            return false;
        }
        // FFXIV：facing は +Z 向きが 0、CCW で正
        var angleRad = MathF.Atan2(dx, dz); // -π..π
        var angleDeg = angleRad * 180f / MathF.PI;
        var diff = NormalizeAngleDeg(angleDeg - FacingDeg);
        return MathF.Abs(diff) <= AngleWidthDeg * 0.5f;
    }

    private static float NormalizeAngleDeg(float deg)
    {
        deg %= 360f;
        if (deg > 180f) deg -= 360f;
        if (deg < -180f) deg += 360f;
        return deg;
    }

    public new static FanShape? FromJson(JsonElement el, SafeZoneContext ctx)
    {
        var origin = ResolvePosition(el, "origin", ctx) ?? ResolvePosition(el, "center", ctx);
        if (origin is null)
        {
            return null;
        }
        return new FanShape
        {
            Origin = origin.Value,
            FacingDeg = GetFloat(el, "facing_deg", 0f),
            AngleWidthDeg = GetFloat(el, "angle_width_deg", 90f),
            Radius = GetFloat(el, "radius", 20f),
        };
    }
}

public sealed class LineShape : TelegraphShape
{
    public Vector3 From { get; init; }
    public Vector3 To { get; init; }
    public float Width { get; init; }

    public override bool Contains(Vector3 p)
    {
        // 線分への XZ 距離。両端は半円キャップで延長扱いせず、線分内のみ判定
        var ax = From.X; var az = From.Z;
        var bx = To.X; var bz = To.Z;
        var dx = bx - ax; var dz = bz - az;
        var lenSq = dx * dx + dz * dz;
        if (lenSq < 0.0001f)
        {
            // 退化：from と to が同じ → 円判定
            var ddx = p.X - ax;
            var ddz = p.Z - az;
            return ddx * ddx + ddz * ddz <= Width * Width * 0.25f;
        }
        var t = ((p.X - ax) * dx + (p.Z - az) * dz) / lenSq;
        if (t < 0f || t > 1f)
        {
            return false;
        }
        var projX = ax + dx * t;
        var projZ = az + dz * t;
        var ddx2 = p.X - projX;
        var ddz2 = p.Z - projZ;
        var halfW = Width * 0.5f;
        return ddx2 * ddx2 + ddz2 * ddz2 <= halfW * halfW;
    }

    public new static LineShape? FromJson(JsonElement el, SafeZoneContext ctx)
    {
        var from = ResolvePosition(el, "from", ctx);
        var to = ResolvePosition(el, "to", ctx);
        if (from is null || to is null)
        {
            return null;
        }
        return new LineShape { From = from.Value, To = to.Value, Width = GetFloat(el, "width", 4f) };
    }
}

public sealed class DonutShape : TelegraphShape
{
    public Vector3 Center { get; init; }
    public float Inner { get; init; }
    public float Outer { get; init; }

    public override bool Contains(Vector3 p)
    {
        var dx = p.X - Center.X;
        var dz = p.Z - Center.Z;
        var distSq = dx * dx + dz * dz;
        return distSq >= Inner * Inner && distSq <= Outer * Outer;
    }

    public new static DonutShape? FromJson(JsonElement el, SafeZoneContext ctx)
    {
        var center = ResolvePosition(el, "center", ctx);
        if (center is null)
        {
            return null;
        }
        return new DonutShape
        {
            Center = center.Value,
            Inner = GetFloat(el, "inner", 5f),
            Outer = GetFloat(el, "outer", 15f),
        };
    }
}

public sealed class RectShape : TelegraphShape
{
    public Vector3 Center { get; init; }
    public float Width { get; init; }
    public float Depth { get; init; }
    public float RotationDeg { get; init; }

    public override bool Contains(Vector3 p)
    {
        // RotationDeg は FFXIV rotation 系（0=南/+Z, 前方=(sinR,cosR)）で FanShape と同一規約。
        // ワールド差分を局所フレームへ写すには +RotationDeg で回す（Depth=z=前方軸）。
        // 旧実装は -RotationDeg で鏡像になり、回転した矩形の前後・左右が入れ替わっていた。
        var rad = RotationDeg * MathF.PI / 180f;
        var cos = MathF.Cos(rad);
        var sin = MathF.Sin(rad);
        var dx = p.X - Center.X;
        var dz = p.Z - Center.Z;
        var lx = dx * cos - dz * sin;
        var lz = dx * sin + dz * cos;
        return MathF.Abs(lx) <= Width * 0.5f && MathF.Abs(lz) <= Depth * 0.5f;
    }

    public new static RectShape? FromJson(JsonElement el, SafeZoneContext ctx)
    {
        var center = ResolvePosition(el, "center", ctx);
        if (center is null)
        {
            return null;
        }
        return new RectShape
        {
            Center = center.Value,
            Width = GetFloat(el, "width", 10f),
            Depth = GetFloat(el, "depth", 5f),
            RotationDeg = GetFloat(el, "rotation_deg", 0f),
        };
    }
}
