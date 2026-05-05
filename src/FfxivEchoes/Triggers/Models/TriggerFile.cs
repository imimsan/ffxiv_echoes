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

    /// <summary>
    /// プレイヤーが事前に書いておくタイムラインノート（軽減タイミング、LB 確認、
    /// 移動指示など）。trigger とは独立に時刻指定で表示・通知する。
    /// </summary>
    [JsonPropertyName("notes")]
    public List<TimelineNote> Notes { get; set; } = new();
}

/// <summary>
/// 戦闘相対秒で配置するメモ。LiveTimeline に表示し、必要なら advance_warning_sec
/// 前に TTS / オーバーレイで通知する。
/// </summary>
public sealed class TimelineNote
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>戦闘相対秒。</summary>
    [JsonPropertyName("time")]
    public double Time { get; set; }

    /// <summary>ライブタイムラインで表示する短文。例：「迅速 + 堅実」。</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    /// <summary>長尺ノート（例：軽減効果時間）。指定があるとバー風に幅を持たせて描画。</summary>
    [JsonPropertyName("duration")]
    public double? Duration { get; set; }

    /// <summary>このノートを表示／通知するロール。空なら全員。
    /// 値は target_resolver の表記に倣う：tank/mt/st/healer/h1/h2/dps/melee/ranged/caster/any。</summary>
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    /// <summary>このノートを表示／通知するジョブ略称（"PLD" 等）。空なら全ジョブ。</summary>
    [JsonPropertyName("job")]
    public string? Job { get; set; }

    /// <summary>"#RRGGBB"。タイムライン上の色付け。</summary>
    [JsonPropertyName("color")]
    public string? Color { get; set; }

    /// <summary>このノートの time の何秒前に TTS / overlay で先行通知するか。
    /// 0 や null なら通知しない（タイムライン表示のみ）。</summary>
    [JsonPropertyName("advance_warning_sec")]
    public double? AdvanceWarningSec { get; set; }

    /// <summary>通知時に読み上げるテキスト。空なら label を使う。</summary>
    [JsonPropertyName("warning_text")]
    public string? WarningText { get; set; }
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
