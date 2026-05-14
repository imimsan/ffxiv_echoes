using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FfxivEchoes.Recording;

/// <summary>
/// 録画 JSONL を読み、戦闘をまたいで再利用するための集計データに変換する純粋ロジック。
/// </summary>
public static class RecordingAggregationReader
{
    private const double OccurrenceClusterWindowSeconds = 6.0;

    /// <summary>
    /// PC スキル / ペット / HP IsPlayer フラグが録画ファイルに書き込まれるようになった
    /// プラグインバージョンの下限（このバージョン以降の録画は IsPlayer による厳密フィルタ済み）。
    /// これより古い録画は <see cref="PcSkillNameFilter"/> の名前ベース fallback に依存するため、
    /// 警告ログで明示する。
    /// </summary>
    private const string MinSupportedPluginVersion = "0.1.0";

    public static AggregatedEvents AggregateFiles(
        IEnumerable<string> paths,
        Action<Exception, string>? onWarning = null)
    {
        var byKey = new Dictionary<EventKey, AggregateBuilder>();
        var battleCount = 0;
        var totalEvents = 0;
        var fileCount = 0;

        foreach (var path in paths)
        {
            fileCount++;
            try
            {
                var hasMeta = false;
                var partyMembers = new PartyIdentity();
                var observationsInFile = new Dictionary<EventKey, List<double>>();
                var partySourceInFile = new HashSet<EventKey>();

                using var stream = RecordingFileIO.OpenReadShared(path);
                using var reader = new StreamReader(stream);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("meta", out var metaProp) && metaProp.GetBoolean())
                        {
                            hasMeta = true;
                            ReadPartyMembers(root, partyMembers);
                            // プラグインバージョン下限チェック：古い録画は IsPlayer フラグ未対応で
                            // PcSkillNameFilter の名前 fallback に依存する。集計は通すが onWarning で通知。
                            if (root.TryGetProperty("plugin_version", out var verEl) &&
                                verEl.ValueKind == JsonValueKind.String)
                            {
                                var ver = verEl.GetString();
                                if (!string.IsNullOrEmpty(ver) && CompareVersions(ver, MinSupportedPluginVersion) < 0)
                                {
                                    onWarning?.Invoke(
                                        new InvalidDataException(
                                            $"古い録画フォーマット (plugin_version={ver} < {MinSupportedPluginVersion})。" +
                                            "IsPlayer フラグ非対応のため PC スキル混入の可能性あり。"),
                                        path);
                                }
                            }
                            continue;
                        }

                        var key = ExtractKey(root);
                        if (key is null)
                        {
                            continue;
                        }

                        totalEvents++;
                        var time = root.TryGetProperty("time", out var t) ? t.GetDouble() : 0;
                        if (!observationsInFile.TryGetValue(key.Value, out var times))
                        {
                            times = new List<double>();
                            observationsInFile.Add(key.Value, times);
                        }
                        times.Add(time);
                        if (IsPartyRelated(key.Value, root, partyMembers))
                        {
                            partySourceInFile.Add(key.Value);
                        }
                    }
                    catch (JsonException ex)
                    {
                        onWarning?.Invoke(ex, path);
                    }
                    catch (InvalidOperationException ex)
                    {
                        onWarning?.Invoke(ex, path);
                    }
                }

                if (hasMeta)
                {
                    battleCount++;
                }

                foreach (var (key, times) in observationsInFile)
                {
                    if (!byKey.TryGetValue(key, out var builder))
                    {
                        builder = new AggregateBuilder(key);
                        byKey.Add(key, builder);
                    }

                    times.Sort();
                    builder.AddFileObservations(times, partySourceInFile.Contains(key));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                onWarning?.Invoke(ex, path);
            }
        }

        var events = byKey.Values
            .Select(b => b.ToEvent())
            .OrderBy(e => e.FirstSeenSeconds)
            .ThenBy(e => e.Key.Type, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new AggregatedEvents(events, battleCount, totalEvents, fileCount);
    }

    private static void ReadPartyMembers(JsonElement root, PartyIdentity partyMembers)
    {
        if (!root.TryGetProperty("party", out var party) || party.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var member in party.EnumerateArray())
        {
            if (!member.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var name = nameEl.GetString();
            if (!string.IsNullOrWhiteSpace(name))
            {
                partyMembers.Names.Add(name);
            }

            if (member.TryGetProperty("object_id", out var objectIdEl) &&
                objectIdEl.ValueKind == JsonValueKind.Number &&
                objectIdEl.TryGetUInt32(out var objectId))
            {
                partyMembers.ObjectIds.Add(objectId);
            }
        }
    }

    private static bool IsPartyRelated(EventKey key, JsonElement evRoot, PartyIdentity partyMembers)
    {
        var rawSource = ReadString(evRoot, "source") ?? ReadString(evRoot, "actor");
        if ((!string.IsNullOrEmpty(key.Source) && partyMembers.Names.Contains(key.Source)) ||
            (!string.IsNullOrEmpty(rawSource) && partyMembers.Names.Contains(rawSource)) ||
            PartyIdMatches(evRoot, "source_id", partyMembers) ||
            PartyIdMatches(evRoot, "actor_id", partyMembers))
        {
            return true;
        }

        // 自己付与 status（source_id == target_id）の扱い：
        //  - party meta が分かっている場合：source/target が PT メンバーの場合のみ「self-applied PC バフ」
        //    として除外する。ボス自己強化（エンレイジ status 等）は source=ボスなので保持される。
        //  - party meta が空 / object_id 不明の古い録画：従来通り「source==target なら PC 自己バフ扱い」で
        //    フォールバック除外（旧録画はこれが唯一の self-buff 識別手段）。
        if (key.Type is "status_gain" or "status_update" or "status_lose" &&
            IsSelfAppliedStatus(evRoot, out var selfId))
        {
            if (partyMembers.ObjectIds.Count > 0)
            {
                // party meta あり：PT メンバーの self-buff のみ除外
                if (partyMembers.ObjectIds.Contains(selfId)) return true;
                // それ以外（ボス自己強化など）はフォールスルーで通常判定へ
            }
            else
            {
                // party meta なし（古い録画）：source==target なら PC 自己バフとみなす
                return true;
            }
        }

        var hasSource =
            !string.IsNullOrEmpty(key.Source) ||
            evRoot.TryGetProperty("source_id", out _) ||
            evRoot.TryGetProperty("source", out _);
        if (hasSource)
        {
            return false;
        }

        return (!string.IsNullOrEmpty(key.Target) && partyMembers.Names.Contains(key.Target)) ||
               PartyIdMatches(evRoot, "target_id", partyMembers);
    }

    /// <summary>
    /// SemVer 風バージョン文字列を比較。a &lt; b なら -1、a == b なら 0、a &gt; b なら 1。
    /// "1.2.3" のような数値 . 区切りのみサポート。それ以外は 0（同等）扱い。
    /// </summary>
    private static int CompareVersions(string a, string b)
    {
        var aParts = a.Split('.', '-', '+');
        var bParts = b.Split('.', '-', '+');
        var len = Math.Max(aParts.Length, bParts.Length);
        for (var i = 0; i < len; i++)
        {
            var ai = i < aParts.Length && int.TryParse(aParts[i], out var an) ? an : 0;
            var bi = i < bParts.Length && int.TryParse(bParts[i], out var bn) ? bn : 0;
            if (ai != bi) return ai < bi ? -1 : 1;
        }
        return 0;
    }

    /// <summary>
    /// status イベントが「self-applied」（source_id == target_id）かを判定。
    /// 一致した場合は <paramref name="selfId"/> に self の id を返す。PC 自己バフはほぼ全てこの条件
    /// に当てはまる（ただしボス自己強化も同条件で当てはまるため、呼び出し側で party meta との
    /// 照合を行う）。
    /// </summary>
    private static bool IsSelfAppliedStatus(JsonElement evRoot, out uint selfId)
    {
        selfId = 0;
        if (!evRoot.TryGetProperty("source_id", out var srcEl) ||
            !evRoot.TryGetProperty("target_id", out var tgtEl)) return false;
        if (srcEl.ValueKind != JsonValueKind.Number || tgtEl.ValueKind != JsonValueKind.Number) return false;
        if (!srcEl.TryGetUInt32(out var s) || !tgtEl.TryGetUInt32(out var t)) return false;
        if (s == 0 || s != t) return false;
        selfId = s;
        return true;
    }

    private static bool PartyIdMatches(JsonElement evRoot, string propertyName, PartyIdentity partyMembers)
    {
        return evRoot.TryGetProperty(propertyName, out var idEl) &&
               idEl.ValueKind == JsonValueKind.Number &&
               idEl.TryGetUInt32(out var id) &&
               partyMembers.ObjectIds.Contains(id);
    }

    private sealed class PartyIdentity
    {
        public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<uint> ObjectIds { get; } = new();
    }

    private static EventKey? ExtractKey(JsonElement evRoot)
    {
        if (!evRoot.TryGetProperty("type", out var typeProp))
        {
            return null;
        }

        var type = typeProp.GetString() ?? string.Empty;
        return type switch
        {
            "cast_start" or "cast_complete" or "cast_cancel" => CastKey(type, evRoot),
            "action_used" => ActionKey(evRoot),
            "status_gain" or "status_lose" or "status_update" => StatusKey(type, evRoot),
            "hp_change" => HpKey(evRoot),
            "object_appear" or "object_disappear" => ObjectKey(type, evRoot),
            "player_pos" => null, // 録画スキャン専用なので集計対象外
            _ => new EventKey(type, null, null, null, null),
        };
    }

    private static EventKey CastKey(string type, JsonElement evRoot)
    {
        var castId = evRoot.TryGetProperty("cast_id", out var c) ? c.GetString() : null;
        var castName = evRoot.TryGetProperty("cast_name", out var n) ? n.GetString() : null;
        return new EventKey(type, castId, castName, ReadSource(evRoot), ReadTarget(evRoot));
    }

    private static EventKey ActionKey(JsonElement evRoot)
    {
        var actionId = evRoot.TryGetProperty("action_id", out var c) ? c.GetString() : null;
        var actionName = evRoot.TryGetProperty("action_name", out var n) ? n.GetString() : null;
        // オートアタックは独立した EventType として扱う。
        // 同じ action_id でも boss/peer ごとに「アタック」が来る周期は別物として可視化したいので
        // タイムライン側で個別に扱えるよう型を分ける。
        var isAutoAttack = evRoot.TryGetProperty("auto_attack", out var aaEl) &&
                           aaEl.ValueKind == JsonValueKind.True;
        if (!isAutoAttack)
        {
            isAutoAttack = IsAutoAttackActionName(actionName);
        }
        var type = isAutoAttack ? "auto_attack" : "action_used";
        return new EventKey(type, actionId, actionName, ReadSource(evRoot), ReadTarget(evRoot));
    }

    private static EventKey StatusKey(string type, JsonElement evRoot)
    {
        var statusId = evRoot.TryGetProperty("status_id", out var i) ? i.GetUInt32().ToString() : null;
        var statusName = evRoot.TryGetProperty("status_name", out var n) ? n.GetString() : null;

        // source: 文字列があればそれ、無ければ source_id を文字列化（PC 由来 status の filtering に使う）。
        // 旧実装は null ハードコードで、self-buff の source 識別がフィルタに届かなかった。
        var source = evRoot.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString()
            : null;
        if (string.IsNullOrEmpty(source) &&
            evRoot.TryGetProperty("source_id", out var sourceId) &&
            sourceId.ValueKind == JsonValueKind.Number)
        {
            source = sourceId.GetUInt32().ToString();
        }

        var target = evRoot.TryGetProperty("target", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null;
        if (string.IsNullOrEmpty(target) &&
            evRoot.TryGetProperty("target_id", out var targetId) &&
            targetId.ValueKind == JsonValueKind.Number)
        {
            target = targetId.GetUInt32().ToString();
        }
        return new EventKey(type, statusId, statusName, source, target);
    }

    private static EventKey HpKey(JsonElement evRoot)
    {
        var actor = evRoot.TryGetProperty("actor", out var a) ? a.GetString() : null;
        return new EventKey("hp_change", null, null, actor, null);
    }

    private static EventKey ObjectKey(string type, JsonElement evRoot)
    {
        string? id = null;
        if (evRoot.TryGetProperty("data_id", out var dataId) && dataId.ValueKind == JsonValueKind.Number)
        {
            id = dataId.GetUInt32().ToString();
        }
        else if (evRoot.TryGetProperty("object_id", out var objectId) && objectId.ValueKind == JsonValueKind.Number)
        {
            id = objectId.GetUInt32().ToString();
        }

        var name = evRoot.TryGetProperty("object_name", out var n) ? n.GetString() : null;
        return new EventKey(type, id, name, name, null);
    }

    private static string? ReadSource(JsonElement evRoot) =>
        ReadString(evRoot, "source") ??
        ReadString(evRoot, "actor") ??
        ReadUInt32String(evRoot, "source_id") ??
        ReadUInt32String(evRoot, "actor_id");

    private static string? ReadTarget(JsonElement evRoot) =>
        ReadString(evRoot, "target") ??
        ReadUInt32String(evRoot, "target_id");

    private static string? ReadString(JsonElement evRoot, string propertyName)
    {
        return evRoot.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string? ReadUInt32String(JsonElement evRoot, string propertyName)
    {
        return evRoot.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetUInt32(out var id)
            ? id.ToString()
            : null;
    }

    private static bool IsAutoAttackActionName(string? actionName)
    {
        if (string.IsNullOrWhiteSpace(actionName))
        {
            return false;
        }

        var normalized = actionName.Trim();
        return string.Equals(normalized, "攻撃", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "Attack", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "Auto Attack", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "Auto-Attack", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class AggregateBuilder
    {
        private readonly List<double> _observedTimesSeconds = new();
        private readonly List<TimedObservation> _observations = new();
        private int _fileIndex;

        public AggregateBuilder(EventKey key)
        {
            Key = key;
        }

        public EventKey Key { get; }
        public bool IsPartySource { get; private set; }

        public void AddFileObservations(IReadOnlyList<double> times, bool isPartySource)
        {
            var fileIndex = _fileIndex++;
            for (var i = 0; i < times.Count; i++)
            {
                var time = times[i];
                _observedTimesSeconds.Add(time);
                _observations.Add(new TimedObservation(fileIndex, time));
            }

            IsPartySource |= isPartySource;
        }

        public AggregatedEvent ToEvent()
        {
            _observedTimesSeconds.Sort();
            var firstSeen = _observedTimesSeconds.Count == 0 ? 0 : _observedTimesSeconds[0];
            var occurrenceClusters = BuildOccurrenceClusters();
            var occurrences = new AggregatedOccurrence[occurrenceClusters.Count];
            for (var i = 0; i < occurrenceClusters.Count; i++)
            {
                var times = occurrenceClusters[i].Times;
                times.Sort();
                occurrences[i] = new AggregatedOccurrence(
                    Index: i,
                    RepresentativeTimeSeconds: Median(times),
                    SeenCount: occurrenceClusters[i].SeenFileCount,
                    ObservedTimesSeconds: times.ToArray());
            }

            return new AggregatedEvent(Key, _observedTimesSeconds.Count, firstSeen)
            {
                ObservedTimesSeconds = _observedTimesSeconds.ToArray(),
                Occurrences = occurrences,
                IsPartySource = IsPartySource,
            };
        }

        private List<OccurrenceCluster> BuildOccurrenceClusters()
        {
            var clusters = new List<OccurrenceCluster>();
            foreach (var observation in _observations.OrderBy(o => o.TimeSeconds))
            {
                OccurrenceCluster? bestCluster = null;
                var bestDistance = double.MaxValue;

                foreach (var cluster in clusters)
                {
                    if (cluster.ContainsFile(observation.FileIndex))
                    {
                        continue;
                    }

                    var distance = Math.Abs(cluster.RepresentativeTimeSeconds - observation.TimeSeconds);
                    if (distance <= OccurrenceClusterWindowSeconds && distance < bestDistance)
                    {
                        bestCluster = cluster;
                        bestDistance = distance;
                    }
                }

                if (bestCluster is null)
                {
                    bestCluster = new OccurrenceCluster();
                    clusters.Add(bestCluster);
                }

                bestCluster.Add(observation);
            }

            return clusters
                .OrderBy(c => c.RepresentativeTimeSeconds)
                .ToList();
        }

        private static double Median(IReadOnlyList<double> values)
        {
            if (values.Count == 0)
            {
                return 0;
            }

            var mid = values.Count / 2;
            if (values.Count % 2 == 1)
            {
                return values[mid];
            }

            return (values[mid - 1] + values[mid]) / 2.0;
        }

        private sealed record TimedObservation(int FileIndex, double TimeSeconds);

        private sealed class OccurrenceCluster
        {
            private readonly HashSet<int> _fileIndexes = new();

            public List<double> Times { get; } = new();

            public int SeenFileCount => _fileIndexes.Count;

            public double RepresentativeTimeSeconds => Median(Times.OrderBy(t => t).ToArray());

            public bool ContainsFile(int fileIndex) => _fileIndexes.Contains(fileIndex);

            public void Add(TimedObservation observation)
            {
                _fileIndexes.Add(observation.FileIndex);
                Times.Add(observation.TimeSeconds);
            }
        }
    }
}
