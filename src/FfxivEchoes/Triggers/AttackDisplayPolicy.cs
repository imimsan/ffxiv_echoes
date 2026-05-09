using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Triggers;

public static class AttackDisplayPolicy
{
    public static AttackDisplayDecision Decide(AutoSettings settings, AttackDisplayRequest request)
    {
        if (request.IsFriendly)
        {
            return AttackDisplayDecision.None;
        }

        if (request.IsAutoAttack)
        {
            return settings.ShowAutoAttacks
                ? AttackDisplayDecision.AutoAttackPulse
                : AttackDisplayDecision.None;
        }

        if (request.HasAoe && settings.ShowAutoTelegraphs)
        {
            return AttackDisplayDecision.AoeTelegraph;
        }

        if (request.IsCast && settings.ShowAllEnemyCasts)
        {
            return AttackDisplayDecision.AttackPulse;
        }

        return AttackDisplayDecision.None;
    }
}

public readonly record struct AttackDisplayRequest(
    bool IsFriendly,
    bool HasAoe,
    bool IsAutoAttack,
    bool IsCast);

public enum AttackDisplayDecision
{
    None,
    AoeTelegraph,
    AttackPulse,
    AutoAttackPulse,
}
