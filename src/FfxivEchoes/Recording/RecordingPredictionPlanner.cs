using System;
using System.Collections.Generic;
using System.Linq;

namespace FfxivEchoes.Recording;

/// <summary>
/// 録画集計から、次戦闘で表示・通知する予測キャスト列を作る。
/// </summary>
public static class RecordingPredictionPlanner
{
    public static IReadOnlyList<RecordingPrediction> BuildCastPredictions(
        AggregatedEvents aggregate,
        IReadOnlySet<string>? coveredCastIds = null,
        IReadOnlySet<string>? partyMembers = null)
    {
        var predictions = new List<RecordingPrediction>();

        foreach (var ev in aggregate.Events)
        {
            // 予測警告は cast_start のみに限定する。
            // action_used は「既に発動した」イベントなので、録画から予測 AoE に使うと
            // 実ダメージ ID / tick / 派生 action を拾って、範囲が大きくズレやすい。
            // cast バー無しギミックはタイムライン下書きや手動 mechanic で扱う。
            if (ev.Key.Type != "cast_start")
            {
                continue;
            }

            if (!string.IsNullOrEmpty(ev.Key.Id) &&
                coveredCastIds is not null &&
                coveredCastIds.Contains(ev.Key.Id))
            {
                continue;
            }

            if (ev.IsPartySource ||
                (!string.IsNullOrEmpty(ev.Key.Source) &&
                 partyMembers is not null &&
                 partyMembers.Contains(ev.Key.Source)))
            {
                continue;
            }

            // PC スキル / ペット名は除外（名前パターン filter）
            var labelForFilter = !string.IsNullOrEmpty(ev.Key.Name) ? ev.Key.Name! : ev.Key.Id ?? string.Empty;
            if (PcSkillNameFilter.LooksLikePcSkillOrPet(labelForFilter, ev.Key.Type))
            {
                continue;
            }

            var label = !string.IsNullOrEmpty(ev.Key.Name)
                ? ev.Key.Name!
                : (!string.IsNullOrEmpty(ev.Key.Id) ? ev.Key.Id! : "?");
            var castId = ev.Key.Id ?? string.Empty;
            var occurrences = GetOccurrences(ev);

            for (var i = 0; i < occurrences.Count; i++)
            {
                var occurrence = occurrences[i];
                predictions.Add(new RecordingPrediction(
                    RelativeSeconds: occurrence.RepresentativeTimeSeconds,
                    Label: label,
                    CastId: castId,
                    Source: ev.Key.Source,
                    ObservedCount: ev.Count,
                    OccurrenceIndex: occurrence.Index,
                    OccurrenceSeenCount: occurrence.SeenCount,
                    Confidence: CalculateConfidence(occurrence.SeenCount, aggregate.BattleCount),
                    TimeJitterSeconds: CalculateJitter(occurrence.ObservedTimesSeconds),
                    EarliestObservedSeconds: MinTime(occurrence.ObservedTimesSeconds, occurrence.RepresentativeTimeSeconds),
                    LatestObservedSeconds: MaxTime(occurrence.ObservedTimesSeconds, occurrence.RepresentativeTimeSeconds)));
            }
        }

        // 同名の重複を集約：FFXIV では同じ技に複数の cast_id が割り当てられるケースが
        // ある（テレグラフ用 ID と実ダメージ用 ID 等）。同 Label が ±1 秒以内に複数並ぶと
        // 「アニア × 2」と冗長表示になる。最も観測回数が多いものを 1 件残してまとめる。
        var deduped = predictions
            .OrderBy(p => p.RelativeSeconds)
            .ThenBy(p => p.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Source, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var result = new List<RecordingPrediction>(deduped.Count);
        var consumed = new bool[deduped.Count];
        const double DedupWindowSec = 1.0;
        for (var i = 0; i < deduped.Count; i++)
        {
            if (consumed[i]) continue;
            var head = deduped[i];
            var bestIdx = i;
            var bestCount = head.ObservedCount;
            for (var j = i + 1; j < deduped.Count; j++)
            {
                if (consumed[j]) continue;
                var c = deduped[j];
                if (c.RelativeSeconds - head.RelativeSeconds > DedupWindowSec) break;
                if (!string.Equals(c.Label, head.Label, StringComparison.OrdinalIgnoreCase)) continue;
                if (!SameSourceForDedup(c.Source, head.Source)) continue;
                consumed[j] = true;
                if (c.ObservedCount > bestCount)
                {
                    bestCount = c.ObservedCount;
                    bestIdx = j;
                }
            }
            result.Add(deduped[bestIdx]);
        }
        return result.ToArray();
    }

    public static IReadOnlyList<RecordingTimelinePrediction> BuildTimelinePredictions(
        AggregatedEvents aggregate,
        IReadOnlySet<string>? coveredCastIds = null,
        IReadOnlySet<string>? partyMembers = null,
        bool includeActions = false,
        bool includeAutoAttacks = false,
        bool includeStatusGains = false,
        bool includeStatusUpdates = false,
        bool includeHpChanges = false,
        bool includeObjects = false)
    {
        var predictions = new List<RecordingTimelinePrediction>();

        foreach (var ev in aggregate.Events)
        {
            if (!IsTimelineCandidate(
                    ev,
                    includeActions,
                    includeAutoAttacks,
                    includeStatusGains,
                    includeStatusUpdates,
                    includeHpChanges,
                    includeObjects))
            {
                continue;
            }

            if (ev.Key.Type == "cast_start" &&
                !string.IsNullOrEmpty(ev.Key.Id) &&
                coveredCastIds is not null &&
                coveredCastIds.Contains(ev.Key.Id))
            {
                continue;
            }

            if (ev.IsPartySource || IsPartyRelated(ev, partyMembers))
            {
                continue;
            }
            // 名前パターン filter：source/id 解決が漏れても PC スキル / ペット名は確実に除外。
            // 古い録画 (IsPlayer フラグ未記録) でも cast_start / action_used を pattern match できる。
            if (PcSkillNameFilter.LooksLikePcSkillOrPet(BuildTimelineLabel(ev.Key), ev.Key.Type))
            {
                continue;
            }

            var occurrences = GetOccurrences(ev);
            for (var i = 0; i < occurrences.Count; i++)
            {
                var occurrence = occurrences[i];
                predictions.Add(new RecordingTimelinePrediction(
                    EventType: ev.Key.Type,
                    RelativeSeconds: occurrence.RepresentativeTimeSeconds,
                    Label: BuildTimelineLabel(ev.Key),
                    Id: ev.Key.Id ?? string.Empty,
                    Source: ev.Key.Source,
                    Target: ev.Key.Target,
                    ObservedCount: ev.Count,
                    OccurrenceIndex: occurrence.Index,
                    OccurrenceSeenCount: occurrence.SeenCount,
                    Confidence: CalculateConfidence(occurrence.SeenCount, aggregate.BattleCount),
                    TimeJitterSeconds: CalculateJitter(occurrence.ObservedTimesSeconds)));
            }
        }

        var sorted = predictions
            .OrderBy(p => p.RelativeSeconds)
            .ThenBy(p => TimelineTypeSort(p.EventType))
            .ThenBy(p => p.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Source, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 同名（cast_id 違い・テレグラフと本体で 2 ID 等）を ±1.0 秒以内で集約。
        // 観測回数が多い方を残す。type が違う場合は別物として残す（cast_start と action_used は分ける）。
        var consumed = new bool[sorted.Count];
        var deduped = new List<RecordingTimelinePrediction>(sorted.Count);
        const double DedupWindowSec = 1.0;
        for (var i = 0; i < sorted.Count; i++)
        {
            if (consumed[i]) continue;
            var head = sorted[i];
            var bestIdx = i;
            var bestCount = head.ObservedCount;
            for (var j = i + 1; j < sorted.Count; j++)
            {
                if (consumed[j]) continue;
                var c = sorted[j];
                if (c.RelativeSeconds - head.RelativeSeconds > DedupWindowSec) break;
                if (!string.Equals(c.EventType, head.EventType, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(c.Label, head.Label, StringComparison.OrdinalIgnoreCase)) continue;
                if (!SameSourceForDedup(c.Source, head.Source)) continue;
                consumed[j] = true;
                if (c.ObservedCount > bestCount)
                {
                    bestCount = c.ObservedCount;
                    bestIdx = j;
                }
            }
            deduped.Add(sorted[bestIdx]);
        }
        return deduped.ToArray();
    }

    public static IReadOnlyList<double> GetObservedTimes(AggregatedEvent ev)
    {
        if (ev.Occurrences.Count > 0)
        {
            return ev.Occurrences.Select(o => o.RepresentativeTimeSeconds).ToArray();
        }

        return ev.ObservedTimesSeconds.Count > 0
            ? ev.ObservedTimesSeconds
            : new[] { ev.FirstSeenSeconds };
    }

    public static IReadOnlyList<AggregatedOccurrence> GetOccurrences(AggregatedEvent ev)
    {
        if (ev.Occurrences.Count > 0)
        {
            return ev.Occurrences;
        }

        if (ev.ObservedTimesSeconds.Count > 0)
        {
            return ev.ObservedTimesSeconds
                .Select((time, index) => new AggregatedOccurrence(index, time, 1, new[] { time }))
                .ToArray();
        }

        return new[]
        {
            new AggregatedOccurrence(0, ev.FirstSeenSeconds, Math.Max(1, ev.Count), new[] { ev.FirstSeenSeconds }),
        };
    }

    public static double FindClosestObservedTime(AggregatedEvent ev, double actualRelSec, double currentOffsetSec)
    {
        var times = GetObservedTimes(ev);
        var best = times[0];
        var bestDistance = Math.Abs((best + currentOffsetSec) - actualRelSec);

        for (var i = 1; i < times.Count; i++)
        {
            var distance = Math.Abs((times[i] + currentOffsetSec) - actualRelSec);
            if (distance < bestDistance)
            {
                best = times[i];
                bestDistance = distance;
            }
        }

        return best;
    }

    private static double CalculateConfidence(int seenCount, int battleCount)
    {
        if (battleCount <= 0)
        {
            return seenCount > 0 ? 1.0 : 0.0;
        }

        return Math.Clamp((double)seenCount / battleCount, 0.0, 1.0);
    }

    private static double CalculateJitter(IReadOnlyList<double> observedTimes)
    {
        if (observedTimes.Count <= 1)
        {
            return 0.0;
        }

        var min = observedTimes[0];
        var max = observedTimes[0];
        for (var i = 1; i < observedTimes.Count; i++)
        {
            min = Math.Min(min, observedTimes[i]);
            max = Math.Max(max, observedTimes[i]);
        }

        return max - min;
    }

    private static double MinTime(IReadOnlyList<double> observedTimes, double fallback)
    {
        if (observedTimes.Count == 0)
        {
            return fallback;
        }

        var min = observedTimes[0];
        for (var i = 1; i < observedTimes.Count; i++)
        {
            min = Math.Min(min, observedTimes[i]);
        }

        return min;
    }

    private static double MaxTime(IReadOnlyList<double> observedTimes, double fallback)
    {
        if (observedTimes.Count == 0)
        {
            return fallback;
        }

        var max = observedTimes[0];
        for (var i = 1; i < observedTimes.Count; i++)
        {
            max = Math.Max(max, observedTimes[i]);
        }

        return max;
    }

    private static bool IsTimelineCandidate(
        AggregatedEvent ev,
        bool includeActions,
        bool includeAutoAttacks,
        bool includeStatusGains,
        bool includeStatusUpdates,
        bool includeHpChanges,
        bool includeObjects)
    {
        return ev.Key.Type switch
        {
            "cast_start" => true,
            "action_used" => includeActions,
            "auto_attack" => includeActions || includeAutoAttacks,
            "status_gain" => includeStatusGains,
            "status_update" => includeStatusUpdates,
            "hp_change" => includeHpChanges,
            "object_appear" => includeObjects,
            _ => false,
        };
    }

    private static bool IsPartyRelated(AggregatedEvent ev, IReadOnlySet<string>? partyMembers)
    {
        if (partyMembers is null)
        {
            return false;
        }

        return (!string.IsNullOrEmpty(ev.Key.Source) && partyMembers.Contains(ev.Key.Source)) ||
               (!string.IsNullOrEmpty(ev.Key.Target) && partyMembers.Contains(ev.Key.Target));
    }

    private static string BuildTimelineLabel(EventKey key)
    {
        if (!string.IsNullOrEmpty(key.Name))
        {
            return key.Name!;
        }

        if (!string.IsNullOrEmpty(key.Id))
        {
            return key.Id!;
        }

        return key.Type switch
        {
            "object_appear" => "object",
            "hp_change" => key.Source ?? "hp",
            _ => key.Type,
        };
    }

    private static int TimelineTypeSort(string type)
    {
        return type switch
        {
            "cast_start" => 0,
            "action_used" => 1,
            "auto_attack" => 2,
            "status_gain" => 3,
            "object_appear" => 4,
            "hp_change" => 5,
            _ => 9,
        };
    }

    private static bool SameSourceForDedup(string? left, string? right)
    {
        var a = string.IsNullOrWhiteSpace(left) ? string.Empty : left.Trim();
        var b = string.IsNullOrWhiteSpace(right) ? string.Empty : right.Trim();
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record RecordingPrediction(
    double RelativeSeconds,
    string Label,
    string CastId,
    string? Source,
    int ObservedCount,
    int OccurrenceIndex,
    int OccurrenceSeenCount,
    double Confidence,
    double TimeJitterSeconds,
    double EarliestObservedSeconds = double.NaN,
    double LatestObservedSeconds = double.NaN);

public sealed record RecordingTimelinePrediction(
    string EventType,
    double RelativeSeconds,
    string Label,
    string Id,
    string? Source,
    string? Target,
    int ObservedCount,
    int OccurrenceIndex,
    int OccurrenceSeenCount,
    double Confidence,
    double TimeJitterSeconds);
