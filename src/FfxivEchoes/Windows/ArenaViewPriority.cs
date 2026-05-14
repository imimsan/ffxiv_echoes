namespace FfxivEchoes.Windows;

public static class ArenaViewPriority
{
    public static int GetDisplayPriority(string? gimmick)
    {
        return gimmick?.ToLowerInvariant() switch
        {
            "half_plane" => 95,
            "cone" => 90,
            "multi_aoe" => 88, // add NPC 同時出現の AoE 群（cone/half_plane の次に重要）
            "outer_ring" => 85,
            "inner_circle" => 85,
            "stack" => 75,
            "scatter" => 75,
            "attack" => 20,
            _ => 0,
        };
    }
}
