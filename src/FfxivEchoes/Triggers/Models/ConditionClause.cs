using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FfxivEchoes.Triggers.Models;

/// <summary>
/// 複合条件のノード（SPEC.md §4.4）。<c>all_of</c> / <c>any_of</c> で再帰可能。
/// </summary>
public sealed class ConditionClause
{
    [JsonPropertyName("target")]
    [JsonConverter(typeof(TargetSpecJsonConverter))]
    public TargetSpec? Target { get; set; }

    [JsonPropertyName("duration_range")]
    public NumericRange? DurationRange { get; set; }

    [JsonPropertyName("stacks")]
    public StacksMatch? Stacks { get; set; }

    [JsonPropertyName("hp_pct")]
    public HpPctRange? HpPct { get; set; }

    [JsonPropertyName("variable")]
    public string? Variable { get; set; }

    [JsonPropertyName("equals")]
    public JsonElement? EqualsValue { get; set; }

    [JsonPropertyName("not_equals")]
    public JsonElement? NotEquals { get; set; }

    [JsonPropertyName("greater_than")]
    public JsonElement? GreaterThan { get; set; }

    [JsonPropertyName("less_than")]
    public JsonElement? LessThan { get; set; }

    [JsonPropertyName("all_of")]
    public List<ConditionClause>? AllOf { get; set; }

    [JsonPropertyName("any_of")]
    public List<ConditionClause>? AnyOf { get; set; }
}
