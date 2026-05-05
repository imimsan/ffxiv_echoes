using FfxivEchoes.Events;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Triggers.Matching;

/// <summary>
/// 複合条件（<see cref="ConditionClause"/>）をイベントに対して評価する。SPEC.md §4.4。
/// </summary>
/// <remarks>
/// all_of / any_of は再帰的に評価。リーフ節は target / duration_range / stacks /
/// hp_pct を該当イベントの値と比較する。variable 比較は F2 範囲外（P1/P2）。
/// </remarks>
public sealed class ConditionEvaluator
{
    private readonly TargetResolver _targetResolver;

    public ConditionEvaluator(TargetResolver targetResolver)
    {
        _targetResolver = targetResolver;
    }

    public bool Evaluate(ConditionClause clause, IGameEvent ev)
    {
        // all_of / any_of は単独 or 組合せでも来る前提（SPEC では排他だが防御的に）
        if (clause.AllOf is { Count: > 0 } allOf)
        {
            foreach (var sub in allOf)
            {
                if (!Evaluate(sub, ev))
                {
                    return false;
                }
            }
            // any_of も併記されていれば追加で評価（AND として連結）
            if (clause.AnyOf is { Count: > 0 } anyOf2)
            {
                if (!EvaluateAnyOf(anyOf2, ev))
                {
                    return false;
                }
            }
            // リーフ条件も併記されていれば追加で評価
            if (HasLeafConstraints(clause) && !EvaluateLeaf(clause, ev))
            {
                return false;
            }
            return true;
        }
        if (clause.AnyOf is { Count: > 0 } anyOf)
        {
            return EvaluateAnyOf(anyOf, ev);
        }
        return EvaluateLeaf(clause, ev);
    }

    private bool EvaluateAnyOf(System.Collections.Generic.List<ConditionClause> clauses, IGameEvent ev)
    {
        foreach (var c in clauses)
        {
            if (Evaluate(c, ev))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasLeafConstraints(ConditionClause c) =>
        c.Target is not null || c.DurationRange is not null ||
        c.Stacks is not null || c.HpPct is not null;

    private bool EvaluateLeaf(ConditionClause clause, IGameEvent ev)
    {
        // target：イベントのターゲット actor ID に対して TargetResolver で照合
        if (clause.Target is { } target)
        {
            var targetId = ExtractTargetId(ev);
            if (targetId == 0 || !_targetResolver.Matches(targetId, target))
            {
                return false;
            }
        }

        // duration_range：status_gain / status_update の RemainingTime に対して
        if (clause.DurationRange is { } durRange)
        {
            var dur = ev switch
            {
                StatusGainedEvent sg => sg.RemainingTime,
                StatusUpdatedEvent su => su.RemainingTime,
                CastStartedEvent cs => cs.CastTime,
                _ => double.NaN,
            };
            if (double.IsNaN(dur))
            {
                return false;
            }
            if (!EventMatcher.MatchRange(durRange, dur))
            {
                return false;
            }
        }

        // stacks：status_gain / status_update の Stacks
        if (clause.Stacks is { } stacks)
        {
            int? actualStacks = ev switch
            {
                StatusGainedEvent sg => sg.Stacks,
                StatusUpdatedEvent su => su.Stacks,
                _ => null,
            };
            if (actualStacks is null)
            {
                return false;
            }
            if (!EventMatcher.MatchStacks(stacks, actualStacks.Value))
            {
                return false;
            }
        }

        // hp_pct：hp_change の HpPct
        if (clause.HpPct is { } hp)
        {
            if (ev is not HpChangedEvent hc)
            {
                return false;
            }
            if (hp.Below is { } below && !(hc.HpPct < below))
            {
                return false;
            }
            if (hp.Above is { } above && !(hc.HpPct > above))
            {
                return false;
            }
        }

        return true;
    }

    private static uint ExtractTargetId(IGameEvent ev) => ev switch
    {
        StatusGainedEvent sg => sg.TargetId,
        StatusLostEvent sl => sl.TargetId,
        StatusUpdatedEvent su => su.TargetId,
        HpChangedEvent hc => hc.ActorId,
        CastStartedEvent cs => cs.TargetId ?? 0,
        _ => 0,
    };
}
