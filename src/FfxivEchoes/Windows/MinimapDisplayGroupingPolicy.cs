namespace FfxivEchoes.Windows;

public static class MinimapDisplayGroupingPolicy
{
    public static bool ShouldLayerAutoAoe(
        uint? autoLuminaCastId,
        float? aoeRadius,
        int? aoeCastType,
        int strategyPositionCount,
        int objectMarkerCount,
        int aoeZoneCount)
    {
        if (autoLuminaCastId is null || autoLuminaCastId == 0)
        {
            return false;
        }

        if (aoeRadius is null or <= 0 || aoeCastType is null)
        {
            return false;
        }

        return strategyPositionCount == 0 &&
               objectMarkerCount == 0 &&
               aoeZoneCount == 0;
    }
}
