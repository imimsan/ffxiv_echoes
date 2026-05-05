using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FfxivEchoes.Triggers.Models;

/// <summary>
/// 安置位置計算定義（SPEC.md §6）。実装は F4〜F7。
/// </summary>
public sealed class SafeZoneCalculation
{
    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    public Dictionary<string, JsonElement>? Params { get; set; }

    [JsonPropertyName("constraints")]
    public List<SafeZoneCalculation>? Constraints { get; set; }

    [JsonPropertyName("output_format")]
    public SafeZoneOutputFormat? OutputFormat { get; set; }
}

public sealed class SafeZoneOutputFormat
{
    [JsonPropertyName("tts_template")]
    public string? TtsTemplate { get; set; }

    [JsonPropertyName("direction_style")]
    public string? DirectionStyle { get; set; }
}
