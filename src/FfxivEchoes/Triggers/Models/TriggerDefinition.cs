using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FfxivEchoes.Triggers.Models;

/// <summary>
/// 単一のトリガー定義（1 件）。
/// </summary>
public sealed class TriggerDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("match")]
    public MatchCondition? Match { get; set; }

    [JsonPropertyName("conditions")]
    public ConditionClause? Conditions { get; set; }

    [JsonPropertyName("actions")]
    public List<ActionDefinition> Actions { get; set; } = new();

    [JsonPropertyName("set_variable")]
    public SetVariable? SetVariable { get; set; }

    [JsonPropertyName("cooldown")]
    public double? Cooldown { get; set; }

    [JsonPropertyName("metadata")]
    public TriggerMetadata? Metadata { get; set; }
}

public sealed class TriggerMetadata
{
    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("last_modified")]
    public DateTimeOffset? LastModified { get; set; }

    [JsonPropertyName("source_logs")]
    public List<string> SourceLogs { get; set; } = new();

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

public sealed class SetVariable
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("operation")]
    public string Operation { get; set; } = "set"; // set / increment / decrement / reset

    [JsonPropertyName("value")]
    public System.Text.Json.JsonElement? Value { get; set; }
}
