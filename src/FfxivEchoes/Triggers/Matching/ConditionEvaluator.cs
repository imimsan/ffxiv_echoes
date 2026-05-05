using System.Text.Json;
using FfxivEchoes.Events;
using FfxivEchoes.Triggers.Models;
using FfxivEchoes.Variables;

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
    private readonly VariableStore? _variables;

    public ConditionEvaluator(TargetResolver targetResolver, VariableStore? variables = null)
    {
        _targetResolver = targetResolver;
        _variables = variables;
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
        c.Stacks is not null || c.HpPct is not null ||
        c.Variable is not null;

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

        // P2: variable 比較
        if (clause.Variable is { Length: > 0 } varName)
        {
            if (_variables is null)
            {
                return false;
            }
            var actual = _variables.Get(varName);
            if (clause.EqualsValue is { } eq && !CompareEquals(actual, eq))
            {
                return false;
            }
            if (clause.NotEquals is { } neq && CompareEquals(actual, neq))
            {
                return false;
            }
            if (clause.GreaterThan is { } gt && !CompareNumeric(actual, gt, (a, b) => a > b))
            {
                return false;
            }
            if (clause.LessThan is { } lt && !CompareNumeric(actual, lt, (a, b) => a < b))
            {
                return false;
            }
        }

        return true;
    }

    private static bool CompareEquals(object? actual, JsonElement expected)
    {
        return expected.ValueKind switch
        {
            JsonValueKind.Number when actual is not null => System.Math.Abs(ToDouble(actual) - expected.GetDouble()) < 1e-9,
            JsonValueKind.String => actual is string s && string.Equals(s, expected.GetString(), System.StringComparison.OrdinalIgnoreCase),
            JsonValueKind.True => actual is bool b1 && b1,
            JsonValueKind.False => actual is bool b2 && !b2,
            JsonValueKind.Null => actual is null,
            _ => false,
        };
    }

    private static bool CompareNumeric(object? actual, JsonElement expected, System.Func<double, double, bool> op)
    {
        if (expected.ValueKind != JsonValueKind.Number || actual is null)
        {
            return false;
        }
        return op(ToDouble(actual), expected.GetDouble());
    }

    private static double ToDouble(object v) => v switch
    {
        double d => d,
        int i => i,
        long l => l,
        float f => f,
        bool b => b ? 1.0 : 0.0,
        string s when double.TryParse(s, out var p) => p,
        _ => 0.0,
    };

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
