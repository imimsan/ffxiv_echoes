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
        return RecordingAggregationReader.AggregateFiles(
            recordings.Select(r => r.Path),
            (ex, path) => _log.Warning(ex, "[FfxivEchoes] 録画ファイル読み込みに失敗：{Path}", path));
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
    double FirstSeenSeconds)
{
    public IReadOnlyList<double> ObservedTimesSeconds { get; init; } = Array.Empty<double>();
    public IReadOnlyList<AggregatedOccurrence> Occurrences { get; init; } = Array.Empty<AggregatedOccurrence>();
    public bool IsPartySource { get; init; }
}

public sealed record AggregatedOccurrence(
    int Index,
    double RepresentativeTimeSeconds,
    int SeenCount,
    IReadOnlyList<double> ObservedTimesSeconds);

public sealed record AggregatedEvents(
    IReadOnlyList<AggregatedEvent> Events,
    int BattleCount,
    int TotalEventCount,
    int RecordingFileCount);

public sealed record RecordingFileInfo(string Path, long Size, DateTime LastModifiedUtc);
