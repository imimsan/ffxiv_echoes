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

    /// <summary>
    /// JsonElement が安置計算オブジェクト（"method" を持つ）なら SafeZoneCalculation へ変換する。
    /// field_marker の "position" / screen_arrow の "to" のように、safe_zone 以外のキーに
    /// 安置計算を書けるスキーマに対し、ハンドラが計算式として解釈するためのフォールバック。
    /// 計算式でない（文字列 / {x,y,z} / null）場合は null を返す。
    /// </summary>
    public static SafeZoneCalculation? FromElement(JsonElement? element)
    {
        if (element is not { } el || el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        if (!el.TryGetProperty("method", out _))
        {
            return null;
        }
        try
        {
            return el.Deserialize<SafeZoneCalculation>();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed class SafeZoneOutputFormat
{
    [JsonPropertyName("tts_template")]
    public string? TtsTemplate { get; set; }

    [JsonPropertyName("direction_style")]
    public string? DirectionStyle { get; set; }
}
