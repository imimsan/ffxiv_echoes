using System;

namespace FfxivEchoes.Triggers;

public readonly record struct KnownAoeGeometrySpec(
    string Shape,
    float RadiusM,
    int CastType,
    float InnerRadiusM,
    float FanDeg,
    float HalfWidthM);

public static class KnownAoeGeometry
{
    public static bool TryCreate(
        AutoSafeCall? safeCall,
        double? radiusOverrideM,
        AutoAoeArenaConfig arena,
        out KnownAoeGeometrySpec spec)
    {
        spec = default;
        if (safeCall is null)
        {
            return false;
        }

        var arenaRadius = Math.Max(1f, (float)arena.ArenaRadius);
        var radius = radiusOverrideM is { } overrideR && overrideR > 0
            ? (float)overrideR
            : DefaultRadius(safeCall.Gimmick, arenaRadius);
        if (radius <= 0)
        {
            return false;
        }

        var shape = (safeCall.Gimmick ?? string.Empty).Trim().ToLowerInvariant();
        spec = shape switch
        {
            "inner_circle" => new KnownAoeGeometrySpec(
                Shape: "circle",
                RadiusM: radius,
                CastType: 5,
                InnerRadiusM: 0f,
                FanDeg: 0f,
                HalfWidthM: 0f),
            "outer_ring" => new KnownAoeGeometrySpec(
                Shape: "donut",
                RadiusM: radius,
                CastType: 6,
                InnerRadiusM: radius * 0.30f,
                FanDeg: 0f,
                HalfWidthM: 0f),
            "cone" => new KnownAoeGeometrySpec(
                Shape: "cone",
                RadiusM: radius,
                CastType: 3,
                InnerRadiusM: 0f,
                FanDeg: (float)(safeCall.FanDeg ?? 90.0),
                HalfWidthM: 0f),
            "half_plane" => new KnownAoeGeometrySpec(
                Shape: "half_plane",
                RadiusM: Math.Max(radius, arenaRadius * 2f),
                CastType: 4,
                InnerRadiusM: 0f,
                FanDeg: 180f,
                HalfWidthM: arenaRadius),
            _ => default,
        };

        return !string.IsNullOrEmpty(spec.Shape);
    }

    private static float DefaultRadius(string? gimmick, float arenaRadius)
    {
        return (gimmick ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "inner_circle" => arenaRadius * 0.55f,
            "outer_ring" => arenaRadius,
            "cone" => arenaRadius,
            "half_plane" => arenaRadius * 2f,
            _ => 0f,
        };
    }
}
