using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Windows.Tabs;

public static class MechanicArenaDefaultsPolicy
{
    public static bool ApplyProfileArena(
        StrategyProfile profile,
        MechanicStrategy mechanic,
        bool includeCenter = true)
    {
        var changed =
            !string.Equals(mechanic.ArenaShape, profile.ArenaShape, StringComparison.Ordinal) ||
            mechanic.ArenaRadius != profile.ArenaRadius ||
            mechanic.ArenaWidth != profile.ArenaWidth ||
            mechanic.ArenaDepth != profile.ArenaDepth ||
            (includeCenter && (
                mechanic.ArenaCenterX != profile.ArenaCenterX ||
                mechanic.ArenaCenterZ != profile.ArenaCenterZ));

        mechanic.ArenaShape = profile.ArenaShape;
        mechanic.ArenaRadius = profile.ArenaRadius;
        mechanic.ArenaWidth = profile.ArenaWidth;
        mechanic.ArenaDepth = profile.ArenaDepth;

        if (includeCenter)
        {
            mechanic.ArenaCenterX = profile.ArenaCenterX;
            mechanic.ArenaCenterZ = profile.ArenaCenterZ;
        }

        return changed;
    }

    public static int ApplyProfileArenaToAll(
        StrategyProfile profile,
        IEnumerable<MechanicStrategy> mechanics,
        bool includeCenter = true)
    {
        var changedCount = 0;
        foreach (var mechanic in mechanics)
        {
            if (ApplyProfileArena(profile, mechanic, includeCenter))
            {
                changedCount++;
            }
        }

        return changedCount;
    }
}
