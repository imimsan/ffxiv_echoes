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

        return predictions
            .OrderBy(p => p.RelativeSeconds)
            .ThenBy(p => p.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<RecordingTimelinePrediction> BuildTimelinePredictions(
        AggregatedEvents aggregate,
        IReadOnlySet<string>? coveredCastIds = null,
        IReadOnlySet<string>? partyMembers = null,
        bool includeActions = false,
        bool includeStatusGains = false,
        bool includeStatusUpdates = false,
        bool includeHpChanges = false)
    {
        var predictions = new List<RecordingTimelinePrediction>();

        foreach (var ev in aggregate.Events)
        {
            if (!IsTimelineCandidate(ev, includeActions, includeStatusGains, includeStatusUpdates, includeHpChanges))
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

        return predictions
            .OrderBy(p => p.RelativeSeconds)
            .ThenBy(p => TimelineTypeSort(p.EventType))
            .ThenBy(p => p.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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
        bool includeStatusGains,
        bool includeStatusUpdates,
        bool includeHpChanges)
    {
        return ev.Key.Type switch
        {
            "cast_start" => true,
            "action_used" => includeActions,
            "status_gain" => includeStatusGains,
            "status_update" => includeStatusUpdates,
            "hp_change" => includeHpChanges,
            "object_appear" => true,
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
            "status_gain" => 2,
            "object_appear" => 3,
            "hp_change" => 4,
            _ => 9,
        };
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
