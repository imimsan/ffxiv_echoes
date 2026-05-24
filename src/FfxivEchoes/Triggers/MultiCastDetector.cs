using System;
using System.Collections.Generic;
using System.Linq;
using FfxivEchoes.Recording;

namespace FfxivEchoes.Triggers;

/// <summary>
/// 録画 aggregate を分析して、ほぼ同時に発火する複数キャストの組合せを検出する。
/// </summary>
/// <remarks>
/// 例：ヒートウィングは boss(0x179C) + 翼1(0x189F) + 翼2(0x189F) が
/// 60ms 以内に発火する。これを検出して「multi_cast」として 1 つのギミックに
/// 集約する。検出結果は SafeCallDictionary に書き戻すこともできる。
/// </remarks>
public sealed class MultiCastDetector
{
    /// <summary>同時発火と見なす時間窓（秒）。</summary>
    public double WindowSeconds { get; set; } = 0.3;

    /// <summary>同時発火と判定する最低共起率（観測回数のうちの割合）。</summary>
    public double MinCoOccurrenceRate { get; set; } = 0.7;

    /// <summary>aggregate を分析して同時発火グループを返す。</summary>
    public IReadOnlyList<MultiCastGroup> Detect(AggregatedEvents agg)
    {
        // 各 cast_id ごとに observed times を取得
        var castEvents = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        foreach (var ev in agg.Events)
        {
            if (ev.Key.Type != "cast_start") continue;
            if (string.IsNullOrEmpty(ev.Key.Id)) continue;
            // RecordingPredictionPlanner.GetObservedTimes は他で使われている
            // ヘルパなので使えるが、ここではシンプルに ObservedTimesSeconds から
            var times = ev.ObservedTimesSeconds is { Count: > 0 } t
                ? t.ToList()
                : new List<double> { ev.FirstSeenSeconds };
            castEvents[ev.Key.Id!] = times;
        }

        var groups = new List<MultiCastGroup>();
        var processedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (castIdA, timesA) in castEvents)
        {
            if (processedKeys.Contains(castIdA)) continue;
            if (timesA.Count == 0) continue;

            var group = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { castIdA };

            foreach (var (castIdB, timesB) in castEvents)
            {
                if (string.Equals(castIdA, castIdB, StringComparison.OrdinalIgnoreCase)) continue;
                if (timesB.Count == 0) continue;

                // A の各観測時刻について、B が ±WindowSeconds 以内に出現した回数を数える
                var coCount = 0;
                foreach (var ta in timesA)
                {
                    if (timesB.Any(tb => Math.Abs(tb - ta) <= WindowSeconds))
                    {
                        coCount++;
                    }
                }
                var rate = (double)coCount / timesA.Count;
                if (rate >= MinCoOccurrenceRate)
                {
                    group.Add(castIdB);
                }
            }

            if (group.Count >= 2)
            {
                // 共起確信度はグループ全体の最低値
                var confidence = ComputeGroupConfidence(group, castEvents);
                groups.Add(new MultiCastGroup(group.ToArray(), confidence));
                foreach (var k in group) processedKeys.Add(k);
            }
            else
            {
                processedKeys.Add(castIdA);
            }
        }

        return groups;
    }

    private double ComputeGroupConfidence(
        HashSet<string> group,
        Dictionary<string, List<double>> castEvents)
    {
        var ids = group.ToArray();
        var minRate = 1.0;
        for (var i = 0; i < ids.Length; i++)
        {
            var timesI = castEvents[ids[i]];
            if (timesI.Count == 0) { minRate = 0; continue; }
            for (var j = 0; j < ids.Length; j++)
            {
                if (i == j) continue;
                var timesJ = castEvents[ids[j]];
                var co = timesI.Count(ta => timesJ.Any(tb => Math.Abs(tb - ta) <= WindowSeconds));
                var rate = (double)co / timesI.Count;
                if (rate < minRate) minRate = rate;
            }
        }
        return minRate;
    }
}

/// <summary>同時発火する cast_id 群と確信度。</summary>
public sealed record MultiCastGroup(IReadOnlyList<string> CastIds, double Confidence);
