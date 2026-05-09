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

                using var stream = File.OpenRead(path);
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
        if ((!string.IsNullOrEmpty(key.Source) && partyMembers.Names.Contains(key.Source)) ||
            PartyIdMatches(evRoot, "source_id", partyMembers) ||
            PartyIdMatches(evRoot, "actor_id", partyMembers))
        {
            return true;
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
            _ => new EventKey(type, null, null, null, null),
        };
    }

    private static EventKey CastKey(string type, JsonElement evRoot)
    {
        var castId = evRoot.TryGetProperty("cast_id", out var c) ? c.GetString() : null;
        var castName = evRoot.TryGetProperty("cast_name", out var n) ? n.GetString() : null;
        // 集計時のキーから source を除外し、同じ cast_id を 1 行に纏める。
        // 同じ cast を複数アクターが同時詠唱（ボス + 翼 ×2 等）しても集計上は 1 件。
        // 発動者情報は recording の raw データに残っているので必要なら参照可能。
        return new EventKey(type, castId, castName, null, null);
    }

    private static EventKey ActionKey(JsonElement evRoot)
    {
        var actionId = evRoot.TryGetProperty("action_id", out var c) ? c.GetString() : null;
        var actionName = evRoot.TryGetProperty("action_name", out var n) ? n.GetString() : null;
        // action_used も同様に source を除外して纏める
        return new EventKey("action_used", actionId, actionName, null, null);
    }

    private static EventKey StatusKey(string type, JsonElement evRoot)
    {
        var statusId = evRoot.TryGetProperty("status_id", out var i) ? i.GetUInt32().ToString() : null;
        var statusName = evRoot.TryGetProperty("status_name", out var n) ? n.GetString() : null;
        var target = evRoot.TryGetProperty("target", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null;
        if (string.IsNullOrEmpty(target) &&
            evRoot.TryGetProperty("target_id", out var targetId) &&
            targetId.ValueKind == JsonValueKind.Number)
        {
            target = targetId.GetUInt32().ToString();
        }
        return new EventKey(type, statusId, statusName, null, target);
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
