using FfxivEchoes.Triggers;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Windows.Tabs;

public static class ArenaCenterDragPolicy
{
    public static void ApplyProfileCenterDelta(
        StrategyProfile profile,
        double deltaX,
        double deltaZ)
    {
        var currentX = profile.ArenaCenterX ?? 0.0;
        var currentZ = profile.ArenaCenterZ ?? 0.0;

        profile.ArenaCenterX = currentX + deltaX;
        profile.ArenaCenterZ = currentZ + deltaZ;
    }

    public static void ApplyMechanicCenterDelta(
        StrategyProfile profile,
        MechanicStrategy mechanic,
        double deltaX,
        double deltaZ)
    {
        var phase = StrategyPlanResolver.GetPhaseSpec(profile, mechanic.Phase);
        var currentX = mechanic.ArenaCenterX ?? phase?.CenterX ?? profile.ArenaCenterX ?? 0.0;
        var currentZ = mechanic.ArenaCenterZ ?? phase?.CenterZ ?? profile.ArenaCenterZ ?? 0.0;

        mechanic.ArenaCenterX = currentX + deltaX;
        mechanic.ArenaCenterZ = currentZ + deltaZ;
    }
}
