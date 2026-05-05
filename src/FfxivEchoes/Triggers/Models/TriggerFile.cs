using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FfxivEchoes.Triggers.Models;

/// <summary>
/// 1 コンテンツぶんのトリガー定義ファイル全体（trigger-schema.json のルート）。
/// </summary>
public sealed class TriggerFile
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0";

    [JsonPropertyName("zone")]
    public string Zone { get; set; } = string.Empty;

    [JsonPropertyName("auto_settings")]
    public AutoSettings AutoSettings { get; set; } = new();

    [JsonPropertyName("metadata")]
    public TriggerFileMetadata? Metadata { get; set; }

    [JsonPropertyName("sync_points")]
    public List<SyncPoint> SyncPoints { get; set; } = new();

    [JsonPropertyName("triggers")]
    public List<TriggerDefinition> Triggers { get; set; } = new();

    [JsonPropertyName("ignored_events")]
    public List<IgnoredEvent> IgnoredEvents { get; set; } = new();
}

public sealed class AutoSettings
{
    [JsonPropertyName("enable_triggers")]
    public bool EnableTriggers { get; set; } = false;

    [JsonPropertyName("auto_record")]
    public bool AutoRecord { get; set; } = false;

    [JsonPropertyName("show_timeline")]
    public bool ShowTimeline { get; set; } = false;
}

public sealed class TriggerFileMetadata
{
    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("last_modified")]
    public DateTimeOffset? LastModified { get; set; }

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

public sealed class SyncPoint
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("cast_id")]
    public string? CastId { get; set; }

    [JsonPropertyName("status_id")]
    public uint? StatusId { get; set; }

    [JsonPropertyName("action_id")]
    public string? ActionId { get; set; }

    [JsonPropertyName("expected_time")]
    public double ExpectedTime { get; set; }

    [JsonPropertyName("tolerance")]
    public double Tolerance { get; set; } = 5.0;
}

public sealed class IgnoredEvent
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("cast_id")]
    public string? CastId { get; set; }

    [JsonPropertyName("status_id")]
    public uint? StatusId { get; set; }

    [JsonPropertyName("action_id")]
    public string? ActionId { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}
