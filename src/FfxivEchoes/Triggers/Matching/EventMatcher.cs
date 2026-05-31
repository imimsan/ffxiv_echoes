using System;
using System.Globalization;
using FfxivEchoes.Events;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Triggers.Matching;

/// <summary>
/// 単一の <see cref="IGameEvent"/> を <see cref="TriggerDefinition"/> と照合する。
/// </summary>
/// <remarks>
/// M6 ではトリガー直下の <c>match</c> フィールドのみをサポート。
/// 複合条件（<c>conditions.all_of</c> / <c>any_of</c>）と <c>set_variable</c>
/// 駆動の分岐は F2 / P2 で対応。
/// </remarks>
public sealed class EventMatcher
{
    private readonly TargetResolver _targetResolver;

    public EventMatcher(TargetResolver targetResolver)
    {
        _targetResolver = targetResolver;
    }

    public bool Matches(TriggerDefinition trigger, IGameEvent ev)
    {
        var typeName = EventTypeName(ev);
        if (string.IsNullOrEmpty(typeName) || trigger.Type != typeName)
        {
            return false;
        }

        var match = trigger.Match;
        return ev switch
        {
            CastStartedEvent x => MatchCastStarted(x, match),
            CastCompletedEvent x => MatchCastEnd(x.SourceName, x.SourceId, x.CastActionId, x.CastActionName, match),
            CastCanceledEvent x => MatchCastEnd(x.SourceName, x.SourceId, x.CastActionId, x.CastActionName, match),
            ActionUsedEvent x => MatchActionUsed(x, match),
            StatusGainedEvent x => MatchStatusGained(x, match),
            StatusLostEvent x => MatchStatusLost(x, match),
            StatusUpdatedEvent x => MatchStatusUpdated(x, match),
            HpChangedEvent x => MatchHpChanged(x, match),
            CombatStartedEvent => true,
            CombatEndedEvent => true,
            ZoneChangedEvent x => MatchZoneChanged(x, match),
            ObjectAppearedEvent x => MatchObjectAppeared(x, match),
            ObjectDisappearedEvent x => MatchObjectDisappeared(x, match),
            _ => false,
        };
    }

    private static bool MatchObjectAppeared(ObjectAppearedEvent ev, MatchCondition? m)
    {
        if (m is null) return true;
        // match.actor を「名前部分一致」として再利用、source_id はオブジェクト ID として一致判定
        if (m.Actor is not null && !ev.ObjectName.Contains(m.Actor)) return false;
        if (m.SourceId is { } expected && ev.ObjectId != expected) return false;
        return true;
    }

    private static bool MatchObjectDisappeared(ObjectDisappearedEvent ev, MatchCondition? m)
    {
        if (m is null) return true;
        if (m.Actor is not null && !ev.ObjectName.Contains(m.Actor)) return false;
        if (m.SourceId is { } expected && ev.ObjectId != expected) return false;
        return true;
    }

    public static string EventTypeName(IGameEvent ev) => ev switch
    {
        CastStartedEvent => "cast_start",
        CastCompletedEvent => "cast_complete",
        CastCanceledEvent => "cast_cancel",
        ActionUsedEvent => "action_used",
        StatusGainedEvent => "status_gain",
        StatusLostEvent => "status_lose",
        StatusUpdatedEvent => "status_update",
        HpChangedEvent => "hp_change",
        CombatStartedEvent => "combat_start",
        CombatEndedEvent => "combat_end",
        ZoneChangedEvent => "zone_change",
        ObjectAppearedEvent => "object_appear",
        ObjectDisappearedEvent => "object_disappear",
        _ => string.Empty,
    };

    // ─── per-type matchers ────────────────────────────────────────────

    private bool MatchCastStarted(CastStartedEvent ev, MatchCondition? m)
    {
        if (m is null)
        {
            return true;
        }
        if (m.CastId is not null && !MatchHexId(m.CastId, ev.CastActionId))
        {
            return false;
        }
        if (m.CastName is not null && !MatchString(m.CastName, ev.CastActionName))
        {
            return false;
        }
        if (m.Source is not null && !MatchString(m.Source, ev.SourceName))
        {
            return false;
        }
        if (m.SourceId is { } sourceId && ev.SourceId != sourceId)
        {
            return false;
        }
        if (m.CastTime is { } range && !MatchRange(range, ev.CastTime))
        {
            return false;
        }
        return true;
    }

    private bool MatchCastEnd(string sourceName, uint sourceId, uint castActionId, string castActionName, MatchCondition? m)
    {
        if (m is null)
        {
            return true;
        }
        if (m.CastId is not null && !MatchHexId(m.CastId, castActionId))
        {
            return false;
        }
        if (m.CastName is not null && !MatchString(m.CastName, castActionName))
        {
            return false;
        }
        if (m.Source is not null && !MatchString(m.Source, sourceName))
        {
            return false;
        }
        if (m.SourceId is { } expected && sourceId != expected)
        {
            return false;
        }
        return true;
    }

    private bool MatchActionUsed(ActionUsedEvent ev, MatchCondition? m)
    {
        if (m is null)
        {
            return true;
        }
        if (m.ActionId is not null && !MatchHexId(m.ActionId, ev.ActionId))
        {
            return false;
        }
        if (m.ActionName is not null && !MatchString(m.ActionName, ev.ActionName))
        {
            return false;
        }
        if (m.Source is not null && !MatchString(m.Source, ev.SourceName))
        {
            return false;
        }
        if (m.SourceId is { } expected && ev.SourceId != expected)
        {
            return false;
        }
        if (m.Target is not null && !_targetResolver.Matches(ev.TargetId ?? 0, m.Target))
        {
            return false;
        }
        return true;
    }

    private bool MatchStatusGained(StatusGainedEvent ev, MatchCondition? m)
    {
        if (m is null)
        {
            return true;
        }
        if (m.StatusId is { } statusId && ev.StatusId != statusId)
        {
            return false;
        }
        if (m.StatusName is not null && !MatchString(m.StatusName, ev.StatusName))
        {
            return false;
        }
        // status_gain の "source"（付与した側）は StatusGainedEvent に名前情報が無いため
        // 文字列照合できない。以前は誤って付与"対象"の TargetName と照合しており、ボス名 source は
        // 永久不発・プレイヤー名は無関係なデバフで誤発火していた。source で絞るなら source_id を使う。
        if (m.SourceId is { } sourceId && ev.SourceId != sourceId)
        {
            return false;
        }
        if (m.Target is not null && !_targetResolver.Matches(ev.TargetId, m.Target))
        {
            return false;
        }
        if (m.DurationRange is { } durRange && !MatchRange(durRange, ev.RemainingTime))
        {
            return false;
        }
        if (m.Stacks is { } stacks && !MatchStacks(stacks, ev.Stacks))
        {
            return false;
        }
        return true;
    }

    private bool MatchStatusLost(StatusLostEvent ev, MatchCondition? m)
    {
        if (m is null)
        {
            return true;
        }
        if (m.StatusId is { } statusId && ev.StatusId != statusId)
        {
            return false;
        }
        if (m.StatusName is not null && !MatchString(m.StatusName, ev.StatusName))
        {
            return false;
        }
        if (m.Target is not null && !_targetResolver.Matches(ev.TargetId, m.Target))
        {
            return false;
        }
        return true;
    }

    private bool MatchStatusUpdated(StatusUpdatedEvent ev, MatchCondition? m)
    {
        if (m is null)
        {
            return true;
        }
        if (m.StatusId is { } statusId && ev.StatusId != statusId)
        {
            return false;
        }
        if (m.Target is not null && !_targetResolver.Matches(ev.TargetId, m.Target))
        {
            return false;
        }
        if (m.DurationRange is { } durRange && !MatchRange(durRange, ev.RemainingTime))
        {
            return false;
        }
        if (m.Stacks is { } stacks && !MatchStacks(stacks, ev.Stacks))
        {
            return false;
        }
        return true;
    }

    private static bool MatchHpChanged(HpChangedEvent ev, MatchCondition? m)
    {
        if (m is null)
        {
            return true;
        }
        if (m.Actor is not null && !MatchString(m.Actor, ev.ActorName))
        {
            return false;
        }
        if (m.HpPct is { Below: { } below } && !(ev.HpPct < below))
        {
            return false;
        }
        if (m.HpPct is { Above: { } above } && !(ev.HpPct > above))
        {
            return false;
        }
        return true;
    }

    private static bool MatchZoneChanged(ZoneChangedEvent ev, MatchCondition? m)
    {
        if (m is null)
        {
            return true;
        }
        // zone_change は match に zone 名を入れたい想定だが、現スキーマには専用フィールドがない。
        // M6 では「zone_change が来た」だけで成立とする（zone 別の絞り込みはトリガーファイル単位で済むため）。
        return true;
    }

    // ─── primitive matchers ───────────────────────────────────────────

    public static bool MatchHexId(string spec, uint actualId)
    {
        if (spec.Length == 0)
        {
            return false;
        }
        var s = spec;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            s = s[2..];
        }
        if (uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed == actualId;
        }
        return false;
    }

    public static bool MatchString(string spec, string actual)
    {
        // 部分一致は混乱を招くので完全一致（大小区別なし）。将来的に正規表現も検討。
        return string.Equals(spec, actual, StringComparison.OrdinalIgnoreCase);
    }

    public static bool MatchRange(NumericRange range, double value)
    {
        if (range.Min is { } min && value < min)
        {
            return false;
        }
        if (range.Max is { } max && value > max)
        {
            return false;
        }
        return true;
    }

    public static bool MatchStacks(StacksMatch spec, int actualStacks)
    {
        if (spec.EqualsValue is { } eq && actualStacks != eq)
        {
            return false;
        }
        if (spec.Min is { } min && actualStacks < min)
        {
            return false;
        }
        if (spec.Max is { } max && actualStacks > max)
        {
            return false;
        }
        return true;
    }
}
