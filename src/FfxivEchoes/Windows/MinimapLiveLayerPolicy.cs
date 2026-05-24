using System;

namespace FfxivEchoes.Windows;

public enum MinimapLiveLayerMode
{
    None,
    SelfOnly,
    Full,
}

public static class MinimapLiveLayerPolicy
{
    public static MinimapLiveLayerMode GetLiveLayerMode(
        string? gimmick,
        int strategyPositionCount,
        int objectMarkerCount,
        int aoeZoneCount)
    {
        var hasUserAuthoredLayout =
            string.Equals(gimmick, "user_layout", StringComparison.OrdinalIgnoreCase) ||
            strategyPositionCount > 0 ||
            objectMarkerCount > 0 ||
            aoeZoneCount > 0;

        return hasUserAuthoredLayout
            ? MinimapLiveLayerMode.SelfOnly
            : MinimapLiveLayerMode.Full;
    }

    public static bool ShouldDrawLivePositionLayer(
        string? gimmick,
        int strategyPositionCount,
        int objectMarkerCount,
        int aoeZoneCount)
    {
        return GetLiveLayerMode(gimmick, strategyPositionCount, objectMarkerCount, aoeZoneCount) ==
               MinimapLiveLayerMode.Full;
    }
}
