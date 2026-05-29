using System.Numerics;

namespace FfxivEchoes.Windows;

public static class ArenaProjection
{
    // 後方互換：正方アリーナ（halfX = halfZ = arenaRadius）として軸独立版へ委譲する。
    public static Vector2 ProjectWorldToMap(
        Vector2 mapCenter,
        float mapRadius,
        Vector3 arenaCenter,
        float arenaRadius,
        Vector3 worldPos)
        => ProjectWorldToMap(mapCenter, mapRadius, arenaCenter, arenaRadius, arenaRadius, worldPos);

    /// <summary>
    /// ワールド座標をミニマップへ投影する軸独立版。非正方矩形アリーナ（halfX ≠ halfZ）で
    /// AoE 原点とプレイヤードット（<see cref="ProjectRelativeToMap"/>）の正規化方式を一致させ、
    /// 短辺方向の安置ズレ（最大 2 倍）を防ぐ（P1-5）。クランプも矩形（軸独立 maxAbs）で統一する。
    /// </summary>
    public static Vector2 ProjectWorldToMap(
        Vector2 mapCenter,
        float mapRadius,
        Vector3 arenaCenter,
        float halfX,
        float halfZ,
        Vector3 worldPos)
    {
        if (halfX <= 0 || halfZ <= 0)
        {
            return mapCenter;
        }

        var nx = (worldPos.X - arenaCenter.X) / halfX;
        var nz = (worldPos.Z - arenaCenter.Z) / halfZ;
        var maxAbs = MathF.Max(MathF.Abs(nx), MathF.Abs(nz));
        if (maxAbs > 1.0f)
        {
            var clamp = 0.97f / maxAbs;
            nx *= clamp;
            nz *= clamp;
        }

        return new Vector2(mapCenter.X + nx * mapRadius, mapCenter.Y + nz * mapRadius);
    }

    public static Vector2 ProjectRelativeToMap(
        Vector2 mapCenter,
        float mapRadius,
        float halfX,
        float halfZ,
        double relativeX,
        double relativeZ)
    {
        if (halfX <= 0 || halfZ <= 0)
        {
            return mapCenter;
        }

        var nx = (float)(relativeX / halfX);
        var nz = (float)(relativeZ / halfZ);
        var maxAbs = MathF.Max(MathF.Abs(nx), MathF.Abs(nz));
        if (maxAbs > 1.0f)
        {
            var clamp = 0.97f / maxAbs;
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

    public static float WorldDirectionalLengthToMap(float worldLength, float arenaRadius, float mapR)
    {
        if (arenaRadius <= 0 || worldLength <= 0 || mapR <= 0) return 0;
        return MathF.Min(mapR * 2.5f, worldLength / arenaRadius * mapR);
    }

    public static bool IsDirectionalAoeCastType(int castType)
        => castType is 3 or 4 or 11 or 12 or 13;

    public static bool UsesFacing(string? gimmick)
    {
        return string.Equals(gimmick, "cone", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(gimmick, "half_plane", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(gimmick, "two_side_cleave", StringComparison.OrdinalIgnoreCase);
    }
}
