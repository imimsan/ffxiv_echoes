using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FfxivEchoes.Triggers.Models;

/// <summary>
/// イベントとのマッチ条件。type に応じて該当フィールドが利用される（SPEC.md §4.2）。
/// </summary>
public sealed class MatchCondition
{
    [JsonPropertyName("cast_id")]
    public string? CastId { get; set; }

    [JsonPropertyName("cast_name")]
    public string? CastName { get; set; }

    [JsonPropertyName("status_id")]
    public uint? StatusId { get; set; }

    [JsonPropertyName("status_name")]
    public string? StatusName { get; set; }

    [JsonPropertyName("action_id")]
    public string? ActionId { get; set; }

    [JsonPropertyName("action_name")]
    public string? ActionName { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("source_id")]
    public uint? SourceId { get; set; }

    [JsonPropertyName("target")]
    [JsonConverter(typeof(TargetSpecJsonConverter))]
    public TargetSpec? Target { get; set; }

    [JsonPropertyName("duration_range")]
    public NumericRange? DurationRange { get; set; }

    [JsonPropertyName("stacks")]
    public StacksMatch? Stacks { get; set; }

    [JsonPropertyName("hp_pct")]
    public HpPctRange? HpPct { get; set; }

    [JsonPropertyName("actor")]
    public string? Actor { get; set; }

    [JsonPropertyName("time")]
    public double? Time { get; set; }

    [JsonPropertyName("tolerance")]
    public double? Tolerance { get; set; }

    [JsonPropertyName("cast_time")]
    public NumericRange? CastTime { get; set; }
}

public sealed class NumericRange
{
    [JsonPropertyName("min")]
    public double? Min { get; set; }

    [JsonPropertyName("max")]
    public double? Max { get; set; }
}

public sealed class StacksMatch
{
    [JsonPropertyName("equals")]
    public int? EqualsValue { get; set; }

    [JsonPropertyName("min")]
    public int? Min { get; set; }

    [JsonPropertyName("max")]
    public int? Max { get; set; }
}

public sealed class HpPctRange
{
    [JsonPropertyName("below")]
    public double? Below { get; set; }

    [JsonPropertyName("above")]
    public double? Above { get; set; }
}

/// <summary>
/// 対象指定（SPEC.md §4.3）。文字列単独 or 文字列の配列。
/// </summary>
public sealed class TargetSpec
{
    public IReadOnlyList<string> Values { get; }

    public TargetSpec(IReadOnlyList<string> values)
    {
        Values = values;
    }

    public bool IsAny(string value)
    {
        foreach (var v in Values)
        {
            if (string.Equals(v, value, System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}

internal sealed class TargetSpecJsonConverter : JsonConverter<TargetSpec>
{
    public override TargetSpec? Read(ref Utf8JsonReader reader, System.Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }
        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            return new TargetSpec(string.IsNullOrEmpty(s) ? new List<string>() : new List<string> { s });
        }
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var list = new List<string>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    break;
                }
                if (reader.TokenType == JsonTokenType.String)
                {
                    var s = reader.GetString();
                    if (!string.IsNullOrEmpty(s))
                    {
                        list.Add(s);
                    }
                }
            }
            return new TargetSpec(list);
        }
        throw new JsonException($"target は string か string[] のみサポート（実際: {reader.TokenType}）");
    }

    public override void Write(Utf8JsonWriter writer, TargetSpec value, JsonSerializerOptions options)
    {
        if (value.Values.Count == 1)
        {
            writer.WriteStringValue(value.Values[0]);
            return;
        }
        writer.WriteStartArray();
        foreach (var v in value.Values)
        {
            writer.WriteStringValue(v);
        }
        writer.WriteEndArray();
    }
}
