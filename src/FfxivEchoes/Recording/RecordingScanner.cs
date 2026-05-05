using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace FfxivEchoes.Recording;

/// <summary>
/// {ConfigDirectory}/recordings/{zone}/*.jsonl をスキャンして、
/// イベントを集計（観測回数や初回出現時刻）する。M8 の集計ビューに使う。
/// </summary>
public sealed class RecordingScanner
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IPluginLog _log;

    public RecordingScanner(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        _pluginInterface = pluginInterface;
        _log = log;
    }

    public IReadOnlyList<string> ListZonesWithRecordings()
    {
        var root = Path.Combine(_pluginInterface.ConfigDirectory.FullName, "recordings");
        if (!Directory.Exists(root))
        {
            return Array.Empty<string>();
        }
        try
        {
            return Directory.EnumerateDirectories(root)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Cast<string>()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[FfxivEchoes] recordings ディレクトリ列挙に失敗");
            return Array.Empty<string>();
        }
    }

    public IReadOnlyList<RecordingFileInfo> ListRecordings(string zoneName)
    {
        var dir = Path.Combine(_pluginInterface.ConfigDirectory.FullName, "recordings", SanitizeSegment(zoneName));
        if (!Directory.Exists(dir))
        {
            return Array.Empty<RecordingFileInfo>();
        }
        try
        {
            return Directory.EnumerateFiles(dir, "*.jsonl")
                .Select(p =>
                {
                    var fi = new FileInfo(p);
                    return new RecordingFileInfo(p, fi.Length, fi.LastWriteTimeUtc);
                })
                .OrderByDescending(r => r.LastModifiedUtc)
                .ToArray();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[FfxivEchoes] recordings の列挙に失敗：{Zone}", zoneName);
            return Array.Empty<RecordingFileInfo>();
        }
    }

    /// <summary>
    /// 指定ゾーンのすべての jsonl からイベントを集計する。M8 の集計ビュー用。
    /// </summary>
    public AggregatedEvents Aggregate(string zoneName)
    {
        var recordings = ListRecordings(zoneName);
        var byKey = new Dictionary<EventKey, AggregatedEvent>();
        var battles = 0;
        var totalEvents = 0;

        foreach (var rec in recordings)
        {
            try
            {
                using var stream = File.OpenRead(rec.Path);
                using var reader = new StreamReader(stream);
                string? line;
                bool hasMeta = false;
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
                            continue;
                        }
                        var key = ExtractKey(root);
                        if (key is null)
                        {
                            continue;
                        }
                        totalEvents++;
                        if (!byKey.TryGetValue(key.Value, out var agg))
                        {
                            var time = root.TryGetProperty("time", out var t) ? t.GetDouble() : 0;
                            agg = new AggregatedEvent(key.Value, 0, time);
                            byKey[key.Value] = agg;
                        }
                        var time2 = root.TryGetProperty("time", out var t2) ? t2.GetDouble() : double.MaxValue;
                        byKey[key.Value] = agg with
                        {
                            Count = agg.Count + 1,
                            FirstSeenSeconds = Math.Min(agg.FirstSeenSeconds, time2),
                        };
                    }
                    catch (JsonException)
                    {
                        // 1 行壊れていてもスキップして続行
                    }
                }
                if (hasMeta)
                {
                    battles++;
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[FfxivEchoes] 録画ファイル読み込みに失敗：{Path}", rec.Path);
            }
        }

        var events = byKey.Values
            .OrderBy(e => e.FirstSeenSeconds)
            .ThenBy(e => e.Key.Type, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new AggregatedEvents(events, battles, totalEvents, recordings.Count);
    }

    /// <summary>
    /// 指定ゾーンの最新録画ファイルの meta 行から party メンバー名を抽出する。
    /// 自分を含む 1〜8 名のリストを返す（取得できなければ空）。
    /// 集計ビューで「自分・PT のイベントを隠す」フィルタに使う。
    /// </summary>
    public IReadOnlyList<string> ListPartyMembers(string zoneName)
    {
        var recordings = ListRecordings(zoneName);
        if (recordings.Count == 0) return Array.Empty<string>();
        // 最新（List は modified 降順想定でないので念のためソート）
        var latest = recordings.OrderByDescending(r => r.LastModifiedUtc).First();
        try
        {
            using var stream = File.OpenRead(latest.Path);
            using var reader = new StreamReader(stream);
            var line = reader.ReadLine();
            if (string.IsNullOrEmpty(line)) return Array.Empty<string>();
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("meta", out var meta) || !meta.GetBoolean())
                return Array.Empty<string>();
            if (!root.TryGetProperty("party", out var partyArr) || partyArr.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();
            var names = new List<string>();
            foreach (var member in partyArr.EnumerateArray())
            {
                if (member.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                {
                    var n = nameEl.GetString();
                    if (!string.IsNullOrEmpty(n)) names.Add(n);
                }
            }
            return names;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[FfxivEchoes] meta party の読み出しに失敗：{Path}", latest.Path);
            return Array.Empty<string>();
        }
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
            "status_gain" or "status_lose" or "status_update" => StatusKey(type, evRoot),
            "hp_change" => HpKey(evRoot),
            _ => new EventKey(type, null, null, null, null),
        };
    }

    private static EventKey CastKey(string type, JsonElement evRoot)
    {
        var castId = evRoot.TryGetProperty("cast_id", out var c) ? c.GetString() : null;
        var castName = evRoot.TryGetProperty("cast_name", out var n) ? n.GetString() : null;
        var source = evRoot.TryGetProperty("source", out var s) ? s.GetString() : null;
        return new EventKey(type, castId, castName, source, null);
    }

    private static EventKey StatusKey(string type, JsonElement evRoot)
    {
        var statusId = evRoot.TryGetProperty("status_id", out var i) ? i.GetUInt32().ToString() : null;
        var statusName = evRoot.TryGetProperty("status_name", out var n) ? n.GetString() : null;
        var target = evRoot.TryGetProperty("target", out var t) ? t.GetString() : null;
        return new EventKey(type, statusId, statusName, null, target);
    }

    private static EventKey HpKey(JsonElement evRoot)
    {
        var actor = evRoot.TryGetProperty("actor", out var a) ? a.GetString() : null;
        return new EventKey("hp_change", null, null, actor, null);
    }

    private static string SanitizeSegment(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "Unknown";
        }
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }
        return sb.ToString();
    }
}

public readonly record struct EventKey(
    string Type,
    string? Id,
    string? Name,
    string? Source,
    string? Target);

public sealed record AggregatedEvent(
    EventKey Key,
    int Count,
    double FirstSeenSeconds);

public sealed record AggregatedEvents(
    IReadOnlyList<AggregatedEvent> Events,
    int BattleCount,
    int TotalEventCount,
    int RecordingFileCount);

public sealed record RecordingFileInfo(string Path, long Size, DateTime LastModifiedUtc);
