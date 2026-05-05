using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FfxivEchoes.Triggers.Models;

/// <summary>
/// トリガー発動時に実行されるアクション（SPEC.md §5）。
/// </summary>
/// <remarks>
/// type に応じて使用するフィールドが変わる。すべての可能なフィールドをここに列挙し、
/// 不要なものは null のままにする方針（実行側 = M7 の ActionDispatcher が type 別に振り分ける）。
/// </remarks>
public sealed class ActionDefinition
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("delay")]
    public double Delay { get; set; } = 0.0;

    // tts / chat_echo / overlay_text 系
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    // tts
    [JsonPropertyName("voice")]
    public string? Voice { get; set; }

    [JsonPropertyName("rate")]
    public double Rate { get; set; } = 1.0;

    // tts / wav / 共通
    [JsonPropertyName("volume")]
    public double Volume { get; set; } = 1.0;

    // wav
    [JsonPropertyName("file")]
    public string? File { get; set; }

    // overlay_text / overlay_corner_text / timer_bar
    [JsonPropertyName("duration")]
    public double? Duration { get; set; }

    [JsonPropertyName("color")]
    public string? Color { get; set; }

    [JsonPropertyName("size")]
    public string? Size { get; set; }

    // overlay_corner_text / field_marker
    [JsonPropertyName("position")]
    public JsonElement? Position { get; set; } // string or {x,y,z}

    // timer_bar
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("warn_at")]
    public double? WarnAt { get; set; }

    // direction_call / screen_arrow / field_marker / proximity_feedback
    [JsonPropertyName("safe_zone")]
    public SafeZoneCalculation? SafeZone { get; set; }

    // direction_call
    [JsonPropertyName("format")]
    public string? Format { get; set; }

    [JsonPropertyName("tts")]
    public bool? Tts { get; set; }

    [JsonPropertyName("overlay")]
    public bool? Overlay { get; set; }

    // screen_arrow
    [JsonPropertyName("from")]
    public string? From { get; set; }

    [JsonPropertyName("to")]
    public JsonElement? To { get; set; } // string or SafeZoneCalculation

    // field_marker
    [JsonPropertyName("shape")]
    public string? Shape { get; set; }

    [JsonPropertyName("radius")]
    public double? Radius { get; set; }

    // proximity_feedback
    [JsonPropertyName("tolerance")]
    public double? Tolerance { get; set; }

    [JsonPropertyName("in_sound")]
    public string? InSound { get; set; }

    [JsonPropertyName("out_sound")]
    public string? OutSound { get; set; }

    [JsonPropertyName("show_distance")]
    public bool? ShowDistance { get; set; }

    // chain_trigger
    [JsonPropertyName("trigger_id")]
    public string? TriggerId { get; set; }

    // 想定外フィールドを失わないための受け皿（フォーマットバージョン跨ぎの後方互換用）
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extras { get; set; }
}
