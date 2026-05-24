using System;
using System.Collections.Generic;
using System.Linq;

namespace FfxivEchoes.SafeZone;

public static class BossSelectionPolicy
{
    public static BossSelection Select(
        IEnumerable<BossCandidate> candidates,
        ulong? preferredSourceId,
        int maxBosses = 4)
    {
        var eligible = candidates
            .Where(c => c.IsEnemy && c.MaxHp > 0)
            .GroupBy(c => c.ObjectId)
            .Select(g => g.First())
            .ToArray();

        var ordered = eligible
            .OrderByDescending(c => preferredSourceId.HasValue && c.ObjectId == preferredSourceId.Value)
            .ThenByDescending(c => c.MaxHp)
            .ThenBy(c => c.ObjectId)
            .Take(Math.Max(1, maxBosses))
            .ToArray();

        return new BossSelection(ordered);
    }
}

public sealed record BossCandidate(
    ulong ObjectId,
    string Name,
    uint MaxHp,
    bool IsEnemy);

public sealed record BossSelection(IReadOnlyList<BossCandidate> Bosses)
{
    public BossCandidate? Primary => Bosses.Count == 0 ? null : Bosses[0];
}
