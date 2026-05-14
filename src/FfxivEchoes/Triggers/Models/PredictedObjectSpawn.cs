using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FfxivEchoes.Triggers.Models;

/// <summary>
/// アリーナ中心相対座標 1 点。<see cref="PredictedObjectSpawn.Positions"/> の要素。
/// 4 体同時出現なら 4 個並ぶ。
/// </summary>
public sealed class SpawnPoint
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("z")]
    public double Z { get; set; }
}

/// <summary>
/// 「特定の cast が起きると、N 秒後にこの object が出現して AoE を発動する」の学習・予告データ。
/// </summary>
/// <remarks>
/// 月の底のパラデイグマのような「召喚系 cast → add 出現 → 即時 AoE」を、Dalamud の ObjectTable
/// 登録遅延（最大 10 秒以上）を待たずに cast 検知時点で先取りして予告描画するための情報。
/// docs/predicted-object-spawn-design.md の §2 と一対一対応。
/// </remarks>
public sealed class PredictedObjectSpawn
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    // ── 起点となる cast ───────────────────────────────

    /// <summary>起点 cast の actionId（"0x67BF" 等）。空なら名前一致のみ。</summary>
    [JsonPropertyName("trigger_cast_id")]
    public string? TriggerCastId { get; set; }

    /// <summary>起点 cast の表示名。UI とログ用。</summary>
    [JsonPropertyName("trigger_cast_name")]
    public string? TriggerCastName { get; set; }

    /// <summary>起点 cast のソース actor 名。任意、不明なら null。</summary>
    [JsonPropertyName("trigger_source_name")]
    public string? TriggerSourceName { get; set; }

    /// <summary>"cast_start" or "cast_complete"。どちらの瞬間に予告を起こすか。</summary>
    [JsonPropertyName("trigger_event")]
    public string TriggerEvent { get; set; } = "cast_start";

    // ── タイミング ───────────────────────────────────

    /// <summary>cast から object 出現までの平均遅延（秒）。学習値。</summary>
    [JsonPropertyName("delay_sec")]
    public double DelaySec { get; set; }

    /// <summary>遅延の観測ばらつき（標準偏差、秒）。0 なら全観測で一致。</summary>
    [JsonPropertyName("delay_sec_jitter")]
    public double DelaySecJitter { get; set; }

    // ── 出現する object ───────────────────────────────

    /// <summary>出現 object の name（"ケツアクアトル" 等）。</summary>
    [JsonPropertyName("object_name")]
    public string ObjectName { get; set; } = string.Empty;

    /// <summary>出現 object の data_id。同名で別個体（変身演出）を区別するキー。</summary>
    [JsonPropertyName("object_data_id")]
    public uint? ObjectDataId { get; set; }

    /// <summary>同時出現する個体数の最頻値（4 体パターンなら 4）。</summary>
    [JsonPropertyName("observed_spawn_count")]
    public int ObservedSpawnCount { get; set; }

    // ── 位置（アリーナ中心相対）───────────────────────

    /// <summary>出現位置のリスト。<see cref="ObservedSpawnCount"/> と同数。</summary>
    [JsonPropertyName("positions")]
    public List<SpawnPoint> Positions { get; set; } = new();

    /// <summary>位置のクラスタ内ばらつき（メートル単位の標準偏差）。</summary>
    [JsonPropertyName("position_variance")]
    public double PositionVariance { get; set; }

    /// <summary>true: 位置が安定（固定座標で予告描画）。false: 位置不定（予告描画せず実出現待ち）。</summary>
    [JsonPropertyName("is_position_stable")]
    public bool IsPositionStable { get; set; } = true;

    // ── AoE 形状 ─────────────────────────────────────

    [JsonPropertyName("shape")]
    public string Shape { get; set; } = "circle";

    [JsonPropertyName("radius_m")]
    public double RadiusM { get; set; }

    [JsonPropertyName("inner_radius_m")]
    public double? InnerRadiusM { get; set; }

    [JsonPropertyName("fan_deg")]
    public double? FanDeg { get; set; }

    [JsonPropertyName("half_width_m")]
    public double? HalfWidthM { get; set; }

    [JsonPropertyName("duration_sec")]
    public double DurationSec { get; set; } = 14.0;

    /// <summary>予告描画の色。確定描画と区別する目的で淡いオレンジ等を既定。</summary>
    [JsonPropertyName("color")]
    public string Color { get; set; } = "#FFA500";

    // ── 信頼度 ───────────────────────────────────────

    /// <summary>このパターンが観測された録画ファイル数。</summary>
    [JsonPropertyName("observed_file_count")]
    public int ObservedFileCount { get; set; }

    /// <summary>全観測回数（複数戦闘の合計）。</summary>
    [JsonPropertyName("observed_total_count")]
    public int ObservedTotalCount { get; set; }

    /// <summary>0.0–1.0 の信頼度。UI 表示用。</summary>
    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    // ── メタデータ ───────────────────────────────────

    /// <summary>"recording" / "manual" / "merged"。手動編集は自動学習で上書きしない。</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = "recording";

    [JsonPropertyName("learned_at")]
    public DateTimeOffset LearnedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("last_observed_at")]
    public DateTimeOffset? LastObservedAt { get; set; }
}
