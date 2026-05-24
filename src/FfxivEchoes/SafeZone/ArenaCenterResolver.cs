using System.Collections.Generic;
using System.Numerics;

namespace FfxivEchoes.SafeZone;

public static class ArenaCenterResolver
{
    private const float TrustFallbackDistance = 12f;

    public static Vector3 Resolve(Vector3 fallbackCenter, IEnumerable<Vector3> anchors)
    {
        var minX = float.PositiveInfinity;
        var maxX = float.NegativeInfinity;
        var minZ = float.PositiveInfinity;
        var maxZ = float.NegativeInfinity;
        var ySum = 0f;
        var count = 0;

        foreach (var p in anchors)
        {
            minX = MathF.Min(minX, p.X);
            maxX = MathF.Max(maxX, p.X);
            minZ = MathF.Min(minZ, p.Z);
            maxZ = MathF.Max(maxZ, p.Z);
            ySum += p.Y;
            count++;
        }

        if (count == 0)
        {
            return fallbackCenter;
        }

        var resolved = new Vector3(
            (minX + maxX) * 0.5f,
            ySum / count,
            (minZ + maxZ) * 0.5f);

        var dx = resolved.X - fallbackCenter.X;
        var dz = resolved.Z - fallbackCenter.Z;
        return dx * dx + dz * dz <= TrustFallbackDistance * TrustFallbackDistance
            ? fallbackCenter
            : resolved;
    }
}
