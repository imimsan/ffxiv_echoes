using System;
using FfxivEchoes.Recording;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Triggers;

/// <summary>
/// TimelineNote の有効な相対秒を解決するヘルパ。
/// AttachedTo（イベント紐付け）が指定されていれば録画 aggregate から
/// 該当イベントの初回観測時刻を引いて使う。なければ Time フィールドを返す。
/// </summary>
public static class TimelineNoteResolver
{
    /// <summary>
    /// ノートの有効な相対秒を返す。AttachedTo が指定されていて、対応するイベントが
    /// 録画に出現していなければ null（タイムライン未配置）。
    /// </summary>
    public static double? ResolveTime(TimelineNote note, AggregatedEvents? recordingAgg)
    {
        if (note.AttachedTo is null)
        {
            return note.Time;
        }

        if (recordingAgg is null)
        {
            return null; // 録画なしでは AttachedTo は解決できない
        }

        var match = note.AttachedTo;

        // type を MatchCondition の代表フィールドから推測：
        //   CastId / CastName → cast_start
        //   StatusId / StatusName → status_gain
        //   ActionId / ActionName → action_used
        string? wantType = null;
        string? wantId = null;
        string? wantName = null;
        if (!string.IsNullOrEmpty(match.CastId) || !string.IsNullOrEmpty(match.CastName))
        {
            wantType = "cast_start";
            wantId = match.CastId;
            wantName = match.CastName;
        }
        else if (match.StatusId is not null || !string.IsNullOrEmpty(match.StatusName))
        {
            wantType = "status_gain";
            wantId = match.StatusId?.ToString();
            wantName = match.StatusName;
        }
        else if (!string.IsNullOrEmpty(match.ActionId) || !string.IsNullOrEmpty(match.ActionName))
        {
            wantType = "action_used";
            wantId = match.ActionId;
            wantName = match.ActionName;
        }
        else if (!string.IsNullOrEmpty(match.Actor))
        {
            wantName = match.Actor;
        }

        if (wantType is null && wantName is null) return null;

        foreach (var ev in recordingAgg.Events)
        {
            if (wantType is not null && !string.Equals(ev.Key.Type, wantType, StringComparison.OrdinalIgnoreCase)) continue;
            if (wantType is null && ev.Key.Type is not ("object_appear" or "object_disappear" or "hp_change"))
            {
                continue;
            }
            if (!string.IsNullOrEmpty(wantId) && !string.Equals(ev.Key.Id, wantId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrEmpty(wantName) &&
                !string.Equals(ev.Key.Name, wantName, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ev.Key.Source, wantName, StringComparison.OrdinalIgnoreCase))
                continue;
            return ResolveOccurrenceTime(ev, note.Time);
        }
        return null;
    }

    private static double ResolveOccurrenceTime(AggregatedEvent ev, double hintTime)
    {
        var times = RecordingPredictionPlanner.GetObservedTimes(ev);
        if (times.Count == 0)
        {
            return ev.FirstSeenSeconds;
        }

        if (hintTime <= 0 || times.Count == 1)
        {
            return times[0];
        }

        var best = times[0];
        var bestDistance = Math.Abs(times[0] - hintTime);
        for (var i = 1; i < times.Count; i++)
        {
            var distance = Math.Abs(times[i] - hintTime);
            if (distance < bestDistance)
            {
                best = times[i];
                bestDistance = distance;
            }
        }

        return best;
    }
}
