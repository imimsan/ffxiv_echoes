using System.Numerics;

namespace FfxivEchoes.Windows;

public static class ArenaProjection
{
    public static Vector2 ProjectWorldToMap(
        Vector2 mapCenter,
        float mapRadius,
        Vector3 arenaCenter,
        float arenaRadius,
        Vector3 worldPos)
    {
        if (arenaRadius <= 0)
        {
            return mapCenter;
        }

        var nx = (worldPos.X - arenaCenter.X) / arenaRadius;
        var nz = (worldPos.Z - arenaCenter.Z) / arenaRadius;
        var dist = MathF.Sqrt(nx * nx + nz * nz);
        if (dist > 1.0f)
        {
            var clamp = 0.97f / dist;
            nx *= clamp;
            nz *= clamp;
        }

        return new Vector2(mapCenter.X + nx * mapRadius, mapCenter.Y + nz * mapRadius);
    }

    public static float RotationToMapAngleRad(float ffxivRotation)
    {
        return MathF.PI / 2f - ffxivRotation;
    }

    public static float ConeRangeScale(double fanDeg)
    {
        return fanDeg >= 175.0 ? 2.4f : 1.45f;
    }

    public static bool ShouldAnchorGimmickToSource(string? gimmick)
    {
        // ターゲット / ソース中心で位置が変わる gimmick は全部 source に固定して描く。
        // outer_ring（外周回避）は基本的にアリーナ全域を覆うので center 固定でよい。
        return string.Equals(gimmick, "cone", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(gimmick, "inner_circle", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(gimmick, "stack", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(gimmick, "attack", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(gimmick, "two_side_cleave", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// AoE の世界半径（メートル）をミニマップ画素半径に変換。
    /// arenaRadius がアリーナ実半径、mapR がミニマップ円の画素半径。
    /// </summary>
    public static float WorldRadiusToMap(float worldRadius, float arenaRadius, float mapR)
    {
        if (arenaRadius <= 0 || worldRadius <= 0) return 0;
        return MathF.Min(mapR * 0.95f, worldRadius / arenaRadius * mapR);
    }

    public static bool UsesFacing(string? gimmick)
    {
        return string.Equals(gimmick, "cone", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(gimmick, "half_plane", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(gimmick, "two_side_cleave", StringComparison.OrdinalIgnoreCase);
    }
}
