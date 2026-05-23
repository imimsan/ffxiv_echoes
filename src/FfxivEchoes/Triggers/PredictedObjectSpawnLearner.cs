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

    private readonly RecordingScanner? _scanner;
    private readonly IDataManager? _dataManager;
    private readonly IPluginLog? _log;

    public PredictedObjectSpawnLearner(RecordingScanner? scanner, IPluginLog? log, IDataManager? dataManager = null)
    {
        _scanner = scanner;
        _log = log;
        _dataManager = dataManager;
    }

    /// <summary>
    /// テスト用 factory：Dalamud サービス不要で学習器を生成する。
    /// Lumina (IDataManager) も無いため、Lumina 由来の形状解決はスキップされる。
    /// </summary>
    public static PredictedObjectSpawnLearner CreateForTesting()
        => new(scanner: null, log: null, dataManager: null);

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
        if (_scanner is null) return Array.Empty<PredictedObjectSpawn>();
        var recordings = _scanner.ListRecordings(zoneName);
        if (recordings.Count == 0)
        {
            return Array.Empty<PredictedObjectSpawn>();
        }
        return LearnFromFilePaths(
            recordings.Select(r => r.Path),
            arenaCenterX,
            arenaCenterZ,
            objectAoeRules,
            zoneName);
    }

    /// <summary>
    /// 録画ファイルのパスを直接受け取って学習する。テスト容易性のため <see cref="LearnFromRecordings"/> から
    /// 分離した本体。本番経路と同じパイプラインを通る。
    /// </summary>
    public IReadOnlyList<PredictedObjectSpawn> LearnFromFilePaths(
        IEnumerable<string> recordingPaths,
        double? arenaCenterX,
        double? arenaCenterZ,
        IReadOnlyList<ObjectAoeRule>? objectAoeRules = null,
        string zoneName = "(unspecified)")
    {
        var paths = recordingPaths.ToList();
        if (paths.Count == 0)
        {
            return Array.Empty<PredictedObjectSpawn>();
        }

        var arenaCx = (float)(arenaCenterX ?? 100.0);
        var arenaCz = (float)(arenaCenterZ ?? 100.0);

        var observations = new Dictionary<ObsKey, List<Observation>>();
        // (object_name, data_id) ごとに「その actor が録画中に発動した action_id の頻度」を集計。
        // 最頻 action を Lumina で解決すれば AoE 形状・半径が判明する。
        var objectActions = new Dictionary<(string Name, uint DataId), Dictionary<uint, int>>();

        foreach (var path in paths)
        {
            try
            {
                ProcessFile(path, arenaCx, arenaCz, observations, objectActions);
            }
            catch (Exception ex)
            {
                _log?.Warning(ex, "[FfxivEchoes] PredictedObjectSpawnLearner: 録画読み込み失敗 {Path}", path);
            }
        }

        var results = new List<PredictedObjectSpawn>();
        foreach (var (key, obs) in observations)
        {
            if (obs.Count < MinObservedTotalCount) continue;
            var fileCount = obs.Select(o => o.SourceFile).Distinct().Count();
            if (fileCount < MinObservedFileCount) continue;

            var spawn = BuildSpawn(key, obs, fileCount, objectAoeRules, objectActions);
            if (spawn is not null)
            {
                results.Add(spawn);
            }
        }

        // 重複排除：同 TriggerCastId + 同 ObjectName で複数 DataId の spawn が同位置に
        // ある場合、最大 DataId（変身後の本物）のみ採用。変身前プレースホルダー (9020 等)
        // による「ドーナツ + 円形が重なる」現象を防ぐ。
        var deduped = DeduplicateTransformedActors(results);

        _log?.Information(
            "[FfxivEchoes] PredictedObjectSpawnLearner: zone={Zone} files={Files} → spawns={Spawns} (dedup: {Before}→{After})",
            zoneName, paths.Count, deduped.Count, results.Count, deduped.Count);
        return deduped;
    }

    /// <summary>
    /// 同 TriggerCastId + ObjectName で複数 DataId の spawn が同位置に重なる場合に最大 DataId だけ残す。
    /// 「変身前 (data_id=9020) と本物 (data_id=14388) のドーナツ+円の重ね描き」の解消用。
    /// </summary>
    private static List<PredictedObjectSpawn> DeduplicateTransformedActors(List<PredictedObjectSpawn> spawns)
    {
        const double positionMatchThresholdM = 2.0;
        var result = new List<PredictedObjectSpawn>();

        foreach (var group in spawns.GroupBy(s => new { s.TriggerCastId, s.ObjectName }))
        {
            var sorted = group.OrderByDescending(s => s.ObjectDataId ?? 0).ToList();
            if (sorted.Count == 1)
            {
                result.Add(sorted[0]);
                continue;
            }

            // 最大 DataId を必ず採用、それより小さい DataId は「位置が同じ」なら捨てる
            var primary = sorted[0];
            result.Add(primary);
            foreach (var candidate in sorted.Skip(1))
            {
                if (!ArePositionsClose(primary.Positions, candidate.Positions, positionMatchThresholdM))
                {
                    result.Add(candidate);  // 別位置のため別個の spawn として保持
                }
            }
        }
        return result;
    }

    private static bool ArePositionsClose(
        IReadOnlyList<SpawnPoint> a,
        IReadOnlyList<SpawnPoint> b,
        double thresholdM)
    {
        if (a.Count != b.Count) return false;
        if (a.Count == 0) return true;
        // 順序非依存：各点の最近接ペアを取る簡易マッチング（点数 4-8 想定なので O(N^2) で十分）
        var matched = new bool[b.Count];
        foreach (var pa in a)
        {
            var bestIdx = -1;
            var bestDist = double.MaxValue;
            for (var i = 0; i < b.Count; i++)
            {
                if (matched[i]) continue;
                var dx = pa.X - b[i].X;
                var dz = pa.Z - b[i].Z;
                var dist = Math.Sqrt(dx * dx + dz * dz);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx = i;
                }
            }
            if (bestIdx < 0 || bestDist > thresholdM) return false;
            matched[bestIdx] = true;
        }
        return true;
    }

    private void ProcessFile(
        string path,
        float arenaCx,
        float arenaCz,
        Dictionary<ObsKey, List<Observation>> observations,
        Dictionary<(string Name, uint DataId), Dictionary<uint, int>> objectActions)
    {
        var castEvents = new List<CastRecord>();
        var objAppearEvents = new List<ObjectAppearRecord>();
        var actionEvents = new List<ActionRecord>();

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
                var objectId = d.RootElement.TryGetProperty("object_id", out var oi) ? oi.GetUInt32() : 0u;
                double x = 0, z = 0;
                if (d.RootElement.TryGetProperty("position", out var pos))
                {
                    if (pos.TryGetProperty("x", out var xe)) x = xe.GetDouble();
                    if (pos.TryGetProperty("z", out var ze)) z = ze.GetDouble();
                }
                objAppearEvents.Add(new ObjectAppearRecord(time, name, dataId, objectId, (float)x, (float)z));
            }
            else if (type == "action_used")
            {
                var isAuto = d.RootElement.TryGetProperty("auto_attack", out var aa) && aa.GetBoolean();
                if (isAuto) continue;
                var sourceId = d.RootElement.TryGetProperty("source_id", out var si) ? si.GetUInt32() : 0u;
                var actionIdStr = d.RootElement.TryGetProperty("action_id", out var ai) ? ai.GetString() ?? "" : "";
                var sourceName = d.RootElement.TryGetProperty("source", out var sn) ? sn.GetString() : null;
                if (TryParseHexUint(actionIdStr, out var actionId))
                {
                    actionEvents.Add(new ActionRecord(time, sourceId, sourceName, actionId));
                }
            }
        }

        // object_appear ごとに「その actor が直後（5 秒以内）に発動した action」を集計
        foreach (var obj in objAppearEvents)
        {
            var nearby = actionEvents
                .Where(a => a.SourceId == obj.ObjectId && a.Time >= obj.Time && (a.Time - obj.Time) < 5.0);
            foreach (var act in nearby)
            {
                var key = (obj.ObjectName, obj.DataId);
                if (!objectActions.TryGetValue(key, out var freq))
                {
                    freq = new Dictionary<uint, int>();
                    objectActions[key] = freq;
                }
                freq[act.ActionId] = freq.GetValueOrDefault(act.ActionId, 0) + 1;
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
                            X = Math.Round((double)(o.X - arenaCx), 3),
                            Z = Math.Round((double)(o.Z - arenaCz), 3),
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
        IReadOnlyList<ObjectAoeRule>? objectAoeRules,
        Dictionary<(string Name, uint DataId), Dictionary<uint, int>> objectActions)
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

        var sourceName = matching
            .Select(o => o.SourceName)
            .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        var triggerEvent = matching.FirstOrDefault()?.CastEvent ?? "cast_start";

        // ID 生成: (CastId, DataId) だけだと「同 cast から同 data_id の別 actor 名」や
        // 「cast_start 版と cast_complete 版」が衝突する。ObjectName の安定 hash と
        // trigger event suffix を加えて衝突を回避する。
        var id = BuildSpawnId(key.CastId, key.DataId, key.ObjectName, triggerEvent);

        // AoE 形状の解決優先順位：
        //   ①object_aoe_rules の DataId 完全一致
        //   ②object_aoe_rules の同名・DataId 指定なし
        //   ③録画から学習した最頻 action_id を Lumina で解決 ← 自動半径学習
        //   ④object_aoe_rules の同名・別 DataId
        //   ⑤fallback (circle/8m)
        var luminaAoe = ResolveLuminaAoeFromActions(objectActions, key.ObjectName, key.DataId);
        var (shape, radius, inner, fan, halfWidth) = ResolveShape(objectAoeRules, key.ObjectName, key.DataId, luminaAoe);

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

    /// <summary>
    /// object_actions から「そのオブジェクトが最も多く発動した action_id」を取り出し、
    /// Lumina (AoeResolver.Resolve) で AoE 情報を取得する。録画ベースの自動半径学習。
    /// <para>
    /// (Name, DataId) **厳密一致**のみ採用。同名で別 DataId（変身体）は合算しない。
    /// 変身前 (data_id=9020) と本物 (data_id=14388) は別 actor として扱われ、
    /// それぞれが発動した action のみ参照される。
    /// </para>
    /// </summary>
    private AoeResolver.AoeInfo? ResolveLuminaAoeFromActions(
        Dictionary<(string Name, uint DataId), Dictionary<uint, int>> objectActions,
        string objectName,
        uint dataId)
    {
        if (_dataManager is null) return null;
        if (!objectActions.TryGetValue((objectName, dataId), out var freq)) return null;
        if (freq is null || freq.Count == 0) return null;

        // 最頻 action_id
        var topAction = freq.OrderByDescending(kv => kv.Value).First();
        if (topAction.Value < 2) return null;  // 1 回だけは信頼度低

        return AoeResolver.Resolve(_dataManager, topAction.Key, _log);
    }

    /// <summary>
    /// 形状解決の優先順位（明示）：
    ///   ① object_aoe_rules で DataId 完全一致がある → 手動編集の確定値として最優先
    ///   ② Lumina 学習結果（録画した action_id から取得した実半径・実形状） ← オレンジ予告の真のサイズ
    ///   ③ object_aoe_rules で DataId 指定なし同名ルール
    ///   ④ object_aoe_rules で同名・別 DataId（変身体等）
    ///   ⑤ fallback (circle/8m)
    /// 注意：enabled に関係なくサイズを参照する。enabled は赤色 AoE 表示の制御のみ。
    /// </summary>
    private static (string shape, double radius, double? inner, double? fan, double? halfWidth) ResolveShape(
        IReadOnlyList<ObjectAoeRule>? rules,
        string objectName,
        uint dataId,
        AoeResolver.AoeInfo? luminaAoe)
    {
        ObjectAoeRule? exactDataIdMatch = null;
        ObjectAoeRule? nameWithoutDataId = null;
        ObjectAoeRule? nameOnly = null;

        if (rules is not null)
        {
            foreach (var rule in rules)
            {
                if (string.IsNullOrWhiteSpace(rule.ObjectName)) continue;
                if (!string.Equals(rule.ObjectName.Trim(), objectName.Trim(), StringComparison.OrdinalIgnoreCase)) continue;

                if (rule.DataId is { } ruleDataId)
                {
                    if (dataId != 0 && ruleDataId == dataId)
                    {
                        exactDataIdMatch = rule;
                        break;
                    }
                    nameOnly ??= rule;
                }
                else
                {
                    nameWithoutDataId ??= rule;
                }
            }
        }

        // ① DataId 完全一致：手動編集された確定値
        if (exactDataIdMatch is not null)
        {
            return (exactDataIdMatch.Shape, exactDataIdMatch.RadiusM, exactDataIdMatch.InnerRadiusM, exactDataIdMatch.FanDeg, exactDataIdMatch.HalfWidthM);
        }

        // ② Lumina 学習結果（録画ベースの実 action 半径） ← 主要なオレンジ予告サイズ源
        if (luminaAoe is not null && luminaAoe.Radius > 0.1f)
        {
            var luminaShape = MapLuminaCastTypeToShape(luminaAoe.CastType);
            var luminaRadius = (double)luminaAoe.Radius;
            double? inner = null;
            if (string.Equals(luminaShape, "donut", StringComparison.OrdinalIgnoreCase))
            {
                inner = luminaRadius * AoeResolver.DonutInnerRatio(luminaAoe.OmenId);
            }
            return (luminaShape, luminaRadius, inner, null, null);
        }

        // ③ 同名・DataId 指定なし
        if (nameWithoutDataId is not null)
        {
            return (nameWithoutDataId.Shape, nameWithoutDataId.RadiusM, nameWithoutDataId.InnerRadiusM, nameWithoutDataId.FanDeg, nameWithoutDataId.HalfWidthM);
        }

        // ④ 同名・別 DataId
        if (nameOnly is not null)
        {
            return (nameOnly.Shape, nameOnly.RadiusM, nameOnly.InnerRadiusM, nameOnly.FanDeg, nameOnly.HalfWidthM);
        }

        // ⑤ fallback
        return ("circle", 8.0, null, null, null);
    }

    private static string MapLuminaCastTypeToShape(int castType)
    {
        return castType switch
        {
            2 or 5 => "circle",
            3 or 13 => "cone",
            4 or 12 => "rect",
            6 or 7 or 10 => "donut",
            11 => "cross",
            _ => "circle",
        };
    }

    private static bool TryParseHexUint(string str, out uint id)
    {
        id = 0;
        if (string.IsNullOrEmpty(str)) return false;
        var s = str;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out id);
    }

    private static double StandardDeviation(IEnumerable<double> values)
    {
        var list = values.ToList();
        if (list.Count <= 1) return 0;
        var avg = list.Average();
        var sumSq = list.Sum(v => (v - avg) * (v - avg));
        return Math.Sqrt(sumSq / (list.Count - 1));
    }

    /// <summary>
    /// PredictedObjectSpawn の安定 ID を生成する。<c>spawn_{castHex}_{dataIdHex}_{nameHash}{eventSuffix}</c>。
    /// ObjectName を含めないと「同 cast から同 data_id の別 actor 名（変身演出の対）」が衝突する。
    /// trigger event suffix は cast_start を既定の無印、cast_complete を <c>_c</c> として区別する。
    /// </summary>
    public static string BuildSpawnId(string castId, uint dataId, string objectName, string triggerEvent)
    {
        var castHex = (castId ?? "").Replace("0x", "", StringComparison.OrdinalIgnoreCase).TrimStart('0');
        if (string.IsNullOrEmpty(castHex)) castHex = "0";
        var nameHash = StableShortHash(objectName ?? "");
        var eventSuffix = string.Equals(triggerEvent, "cast_complete", StringComparison.OrdinalIgnoreCase) ? "_c" : "";
        return $"spawn_{castHex}_{dataId:X}_{nameHash}{eventSuffix}";
    }

    /// <summary>
    /// 文字列の 4-hex 文字安定 hash。<see cref="string.GetHashCode()"/> はランタイム間で変動するため
    /// 永続化される ID には使えない。FNV-1a 32bit を使い下位 16bit を採用。
    /// </summary>
    private static string StableShortHash(string s)
    {
        const uint offsetBasis = 2166136261u;
        const uint prime = 16777619u;
        var hash = offsetBasis;
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        foreach (var b in bytes)
        {
            hash ^= b;
            hash *= prime;
        }
        return (hash & 0xFFFF).ToString("X4");
    }

    private readonly record struct ObsKey(string CastId, string CastName, string ObjectName, uint DataId);

    private sealed record CastRecord(double Time, string EventType, string CastId, string CastName, string? SourceName);
    private sealed record ObjectAppearRecord(double Time, string ObjectName, uint DataId, uint ObjectId, float X, float Z);
    private sealed record ActionRecord(double Time, uint SourceId, string? SourceName, uint ActionId);

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
