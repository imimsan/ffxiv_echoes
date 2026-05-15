using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin.Services;
using FfxivEchoes.Recording;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Triggers;

/// <summary>
/// 録画 jsonl から「Cast → N 秒後に Object 出現」ペアを抽出し、<see cref="PredictedObjectSpawn"/>
/// として学習する。<c>docs/predicted-object-spawn-design.md</c> §3 と一対一対応。
/// </summary>
/// <remarks>
/// 入力：<see cref="RecordingScanner.ListRecordings"/> で取れる jsonl 群。
/// 出力：(cast_id, object_name, object_data_id) ごとに集計された PredictedObjectSpawn リスト。
///
/// マージは呼び元（攻略登録タブ or 起動時バッチ）の責務。Source="manual" の手動編集は
/// この学習器の出力では上書きしないこと。
/// </remarks>
public sealed class PredictedObjectSpawnLearner
{
    /// <summary>cast から object 出現までを「同じパターン」と扱う最大遅延（秒）。</summary>
    private const double CastObjectPairWindowSec = 30.0;

    /// <summary>同時出現と見なす時刻差の許容範囲（秒）。</summary>
    private const double SameTimeEpsSec = 0.5;

    /// <summary>学習結果として保持する最低ファイル数。これ未満は捨てる。</summary>
    private const int MinObservedFileCount = 2;

    /// <summary>学習結果として保持する最低観測回数。これ未満は捨てる。</summary>
    private const int MinObservedTotalCount = 2;

    /// <summary>位置が「安定」と判定する標準偏差（メートル）。これを超えると不定扱い。</summary>
    private const double PositionStableThresholdM = 1.5;

    private readonly RecordingScanner _scanner;
    private readonly IPluginLog _log;

    public PredictedObjectSpawnLearner(RecordingScanner scanner, IPluginLog log)
    {
        _scanner = scanner;
        _log = log;
    }

    /// <summary>
    /// 指定ゾーンの全録画から学習し、PredictedObjectSpawn のリストを返す。
    /// </summary>
    /// <param name="zoneName">対象ゾーン名</param>
    /// <param name="arenaCenterX">アリーナ中心 X（相対座標化に使用）</param>
    /// <param name="arenaCenterZ">アリーナ中心 Z</param>
    /// <param name="objectAoeRules">既存の AoE 形状ルール群。形状/半径/内径を継承するため使う。</param>
    public IReadOnlyList<PredictedObjectSpawn> LearnFromRecordings(
        string zoneName,
        double? arenaCenterX,
        double? arenaCenterZ,
        IReadOnlyList<ObjectAoeRule>? objectAoeRules = null)
    {
        var recordings = _scanner.ListRecordings(zoneName);
        if (recordings.Count == 0)
        {
            return Array.Empty<PredictedObjectSpawn>();
        }

        var arenaCx = (float)(arenaCenterX ?? 100.0);
        var arenaCz = (float)(arenaCenterZ ?? 100.0);

        var observations = new Dictionary<ObsKey, List<Observation>>();

        foreach (var rec in recordings)
        {
            try
            {
                ProcessFile(rec.Path, arenaCx, arenaCz, observations);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[FfxivEchoes] PredictedObjectSpawnLearner: 録画読み込み失敗 {Path}", rec.Path);
            }
        }

        var results = new List<PredictedObjectSpawn>();
        foreach (var (key, obs) in observations)
        {
            if (obs.Count < MinObservedTotalCount) continue;
            var fileCount = obs.Select(o => o.SourceFile).Distinct().Count();
            if (fileCount < MinObservedFileCount) continue;

            var spawn = BuildSpawn(key, obs, fileCount, objectAoeRules);
            if (spawn is not null)
            {
                results.Add(spawn);
            }
        }

        _log.Information(
            "[FfxivEchoes] PredictedObjectSpawnLearner: zone={Zone} files={Files} → spawns={Spawns}",
            zoneName, recordings.Count, results.Count);
        return results;
    }

    private void ProcessFile(
        string path,
        float arenaCx,
        float arenaCz,
        Dictionary<ObsKey, List<Observation>> observations)
    {
        var castEvents = new List<CastRecord>();
        var objAppearEvents = new List<ObjectAppearRecord>();

        using var stream = RecordingFileIO.OpenReadShared(path);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch { continue; }
            using var d = doc;

            if (!d.RootElement.TryGetProperty("type", out var typeEl)) continue;
            var type = typeEl.GetString();
            if (string.IsNullOrEmpty(type)) continue;

            if (!d.RootElement.TryGetProperty("time", out var timeEl)) continue;
            var time = timeEl.GetDouble();

            if (type == "cast_start" || type == "cast_complete")
            {
                var castId = d.RootElement.TryGetProperty("cast_id", out var ci) ? ci.GetString() ?? "" : "";
                var castName = d.RootElement.TryGetProperty("cast_name", out var cn) ? cn.GetString() ?? "" : "";
                var source = d.RootElement.TryGetProperty("source", out var sr) ? sr.GetString() : null;
                castEvents.Add(new CastRecord(time, type, castId, castName, source));
            }
            else if (type == "object_appear")
            {
                var name = d.RootElement.TryGetProperty("object_name", out var on) ? on.GetString() ?? "" : "";
                var dataId = d.RootElement.TryGetProperty("data_id", out var di) ? di.GetUInt32() : 0u;
                double x = 0, z = 0;
                if (d.RootElement.TryGetProperty("position", out var pos))
                {
                    if (pos.TryGetProperty("x", out var xe)) x = xe.GetDouble();
                    if (pos.TryGetProperty("z", out var ze)) z = ze.GetDouble();
                }
                objAppearEvents.Add(new ObjectAppearRecord(time, name, dataId, (float)x, (float)z));
            }
        }

        // cast → object 出現のペアリング
        foreach (var cast in castEvents)
        {
            // 30 秒以内の object_appear を収集（cast 後のみ）
            var nearby = objAppearEvents
                .Where(o => o.Time >= cast.Time && (o.Time - cast.Time) <= CastObjectPairWindowSec)
                .OrderBy(o => o.Time)
                .ToList();
            if (nearby.Count == 0) continue;

            // 同時刻 (誤差 < 0.5 秒) のグループに分割
            var groups = new List<List<ObjectAppearRecord>>();
            foreach (var obj in nearby)
            {
                var lastGroup = groups.LastOrDefault();
                if (lastGroup is not null && obj.Time - lastGroup[0].Time < SameTimeEpsSec)
                {
                    lastGroup.Add(obj);
                }
                else
                {
                    groups.Add(new List<ObjectAppearRecord> { obj });
                }
            }

            // 各グループ内で (object_name, data_id) ごとに集計
            foreach (var group in groups)
            {
                var byName = group.GroupBy(o => (o.ObjectName, o.DataId));
                foreach (var nameGroup in byName)
                {
                    var members = nameGroup.ToList();
                    if (members.Count == 0) continue;
                    var first = members[0];
                    var key = new ObsKey(cast.CastId, cast.CastName, first.ObjectName, first.DataId);
                    if (!observations.TryGetValue(key, out var list))
                    {
                        list = new List<Observation>();
                        observations[key] = list;
                    }

                    var positions = members
                        .Select(o => new SpawnPoint
                        {
                            X = Math.Round(o.X - arenaCx, 3),
                            Z = Math.Round(o.Z - arenaCz, 3),
                        })
                        .ToList();
                    var delay = group[0].Time - cast.Time;

                    list.Add(new Observation
                    {
                        DelaySec = delay,
                        Positions = positions,
                        Count = members.Count,
                        SourceFile = path,
                        SourceName = cast.SourceName,
                        CastEvent = cast.EventType,
                    });
                }
            }
        }
    }

    private PredictedObjectSpawn? BuildSpawn(
        ObsKey key,
        List<Observation> obs,
        int fileCount,
        IReadOnlyList<ObjectAoeRule>? objectAoeRules)
    {
        // 同時出現体数の最頻値
        var spawnCount = obs
            .GroupBy(o => o.Count)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Key)
            .First()
            .Key;

        // 最頻値と一致する観測だけで位置統計
        var matching = obs.Where(o => o.Count == spawnCount).ToList();
        if (matching.Count == 0) return null;

        // 遅延統計
        var delays = matching.Select(o => o.DelaySec).ToList();
        var avgDelay = delays.Average();
        var delayStd = StandardDeviation(delays);

        // 位置クラスタリング（簡易）：各観測の Positions をソートしてインデックスごとに集計。
        // 4 体出現なら 4 つのクラスタを作る。
        var clusterCenters = new List<SpawnPoint>();
        var clusterStds = new List<double>();

        for (var clusterIdx = 0; clusterIdx < spawnCount; clusterIdx++)
        {
            // 各観測の Positions を X 昇順 → Z 昇順でソートしてから clusterIdx を取る
            var pointsAtIndex = matching
                .Select(o => o.Positions
                    .OrderBy(p => p.X)
                    .ThenBy(p => p.Z)
                    .ElementAt(clusterIdx))
                .ToList();
            var avgX = pointsAtIndex.Average(p => p.X);
            var avgZ = pointsAtIndex.Average(p => p.Z);
            var stdX = StandardDeviation(pointsAtIndex.Select(p => p.X));
            var stdZ = StandardDeviation(pointsAtIndex.Select(p => p.Z));
            clusterCenters.Add(new SpawnPoint
            {
                X = Math.Round(avgX, 3),
                Z = Math.Round(avgZ, 3),
            });
            clusterStds.Add(Math.Sqrt(stdX * stdX + stdZ * stdZ));
        }

        var maxStd = clusterStds.Count > 0 ? clusterStds.Max() : 0;
        var isStable = maxStd < PositionStableThresholdM;

        // 信頼度：ファイル数 5 で 1.0 に飽和、位置不定なら 0.5 倍
        var confidence = Math.Min(1.0, fileCount / 5.0) * (isStable ? 1.0 : 0.5);

        // ID 生成
        var castIdHex = key.CastId.Replace("0x", "", StringComparison.OrdinalIgnoreCase).TrimStart('0');
        if (string.IsNullOrEmpty(castIdHex)) castIdHex = "0";
        var id = $"spawn_{castIdHex}_{key.DataId:X}";

        var sourceName = matching
            .Select(o => o.SourceName)
            .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        var triggerEvent = matching.FirstOrDefault()?.CastEvent ?? "cast_start";

        // AoE 形状は object_aoe_rules から同名 + DataId 一致を優先して継承。
        // 見つからなければ円形 5m のフォールバック（旧挙動）。
        var (shape, radius, inner, fan, halfWidth) = ResolveShapeFromRules(objectAoeRules, key.ObjectName, key.DataId);

        return new PredictedObjectSpawn
        {
            Id = id,
            Enabled = true,
            TriggerCastId = key.CastId,
            TriggerCastName = key.CastName,
            TriggerSourceName = sourceName,
            TriggerEvent = triggerEvent,
            DelaySec = Math.Round(avgDelay, 3),
            DelaySecJitter = Math.Round(delayStd, 3),
            ObjectName = key.ObjectName,
            ObjectDataId = key.DataId == 0 ? null : key.DataId,
            ObservedSpawnCount = spawnCount,
            Positions = clusterCenters,
            PositionVariance = Math.Round(maxStd, 3),
            IsPositionStable = isStable,
            Shape = shape,
            RadiusM = radius,
            InnerRadiusM = inner,
            FanDeg = fan,
            HalfWidthM = halfWidth,
            DurationSec = 8.0,
            Color = "#FFA500",
            ObservedFileCount = fileCount,
            ObservedTotalCount = matching.Count,
            Confidence = Math.Round(confidence, 3),
            Source = "recording",
            LearnedAt = DateTimeOffset.UtcNow,
            LastObservedAt = DateTimeOffset.UtcNow,
        };
    }

    private static (string shape, double radius, double? inner, double? fan, double? halfWidth) ResolveShapeFromRules(
        IReadOnlyList<ObjectAoeRule>? rules,
        string objectName,
        uint dataId)
    {
        if (rules is null)
        {
            return ("circle", 5.0, null, null, null);
        }

        // DataId 一致を優先。次に名前一致。
        ObjectAoeRule? best = null;
        foreach (var rule in rules)
        {
            if (!rule.Enabled) continue;
            if (string.IsNullOrWhiteSpace(rule.ObjectName)) continue;
            var nameMatch = string.Equals(rule.ObjectName.Trim(), objectName.Trim(), StringComparison.OrdinalIgnoreCase);
            if (!nameMatch) continue;

            // DataId 一致なら即採用
            if (rule.DataId is { } ruleDataId && dataId != 0 && ruleDataId == dataId)
            {
                return (rule.Shape, rule.RadiusM, rule.InnerRadiusM, rule.FanDeg, rule.HalfWidthM);
            }
            // DataId 指定なしルール（名前のみ）は候補に
            if (rule.DataId is null && best is null)
            {
                best = rule;
            }
        }

        if (best is not null)
        {
            return (best.Shape, best.RadiusM, best.InnerRadiusM, best.FanDeg, best.HalfWidthM);
        }
        return ("circle", 5.0, null, null, null);
    }

    private static double StandardDeviation(IEnumerable<double> values)
    {
        var list = values.ToList();
        if (list.Count <= 1) return 0;
        var avg = list.Average();
        var sumSq = list.Sum(v => (v - avg) * (v - avg));
        return Math.Sqrt(sumSq / (list.Count - 1));
    }

    private readonly record struct ObsKey(string CastId, string CastName, string ObjectName, uint DataId);

    private sealed record CastRecord(double Time, string EventType, string CastId, string CastName, string? SourceName);
    private sealed record ObjectAppearRecord(double Time, string ObjectName, uint DataId, float X, float Z);

    private sealed class Observation
    {
        public double DelaySec { get; set; }
        public List<SpawnPoint> Positions { get; set; } = new();
        public int Count { get; set; }
        public string SourceFile { get; set; } = string.Empty;
        public string? SourceName { get; set; }
        public string CastEvent { get; set; } = "cast_start";
    }
}
