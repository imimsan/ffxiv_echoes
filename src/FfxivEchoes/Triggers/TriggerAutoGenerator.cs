using System;
using System.Collections.Generic;
using System.Text;
using Dalamud.Plugin.Services;
using FfxivEchoes.Recording;
using FfxivEchoes.Triggers.Models;
using FfxivEchoes.Windows;

namespace FfxivEchoes.Triggers;

public sealed class TriggerAutoGenerator
{
    private readonly IDataManager _dataManager;
    private readonly IPluginLog _log;

    public TriggerAutoGenerator(IDataManager dataManager, IPluginLog log)
    {
        _dataManager = dataManager;
        _log = log;
    }

    public GenerationResult Generate(
        AggregatedEvents agg,
        IReadOnlyList<TriggerDefinition> existing,
        IReadOnlyList<string>? partyMembers = null)
    {
        var existingCastIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var trig in existing)
        {
            if (trig.Match?.CastId is { Length: > 0 } cid)
            {
                existingCastIds.Add(cid);
            }
        }

        var partySet = partyMembers is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(partyMembers, StringComparer.OrdinalIgnoreCase);
        var generatedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var generated = new List<TriggerDefinition>();
        var skipped = new List<string>();

        foreach (var ev in agg.Events)
        {
            if (ev.Key.Type != "cast_start") continue;
            if (string.IsNullOrEmpty(ev.Key.Id)) continue;

            if (!string.IsNullOrEmpty(ev.Key.Source) && partySet.Contains(ev.Key.Source))
            {
                continue;
            }

            if (existingCastIds.Contains(ev.Key.Id))
            {
                skipped.Add($"{ev.Key.Name ?? ev.Key.Id} (existing)");
                continue;
            }

            var trigger = BuildTrigger(ev, generatedIds);
            if (trigger is not null)
            {
                generated.Add(trigger);
            }
        }

        return new GenerationResult(generated, skipped);
    }

    private TriggerDefinition? BuildTrigger(AggregatedEvent ev, ISet<string> generatedIds)
    {
        if (!AoeResolver.TryParseCastId(ev.Key.Id!, out var actionId))
        {
            return null;
        }

        var castName = ev.Key.Name ?? ev.Key.Id ?? "?";
        var trigger = new TriggerDefinition
        {
            Id = MakeUniqueId(actionId, ev, generatedIds),
            Name = $"{castName} (auto)",
            Enabled = true,
            Type = "cast_start",
            Match = new MatchCondition
            {
                CastId = ev.Key.Id,
                CastName = ev.Key.Name,
                Source = ev.Key.Source,
            },
        };

        trigger.Actions.Add(new ActionDefinition { Type = "tts", Text = castName });

        var knownSafeCall = AutoSafeCallPlanner.CreateKnown(actionId, castName);
        var aoe = AoeResolver.Resolve(_dataManager, actionId, _log);
        var safeCall = knownSafeCall ?? (aoe is null ? null : AutoSafeCallPlanner.Create(aoe, castName));
        if (safeCall is not null)
        {
            trigger.Actions.Add(new ActionDefinition
            {
                Type = "arena_view",
                Gimmick = safeCall.Gimmick,
                Direction = ArenaProjection.UsesFacing(safeCall.Gimmick) ? "N" : null,
                FanDeg = safeCall.FanDeg,
                Callout = safeCall.Callout,
                Duration = 5.0,
                ArenaRadius = 20.0,
            });
        }

        return trigger;
    }

    private static string MakeUniqueId(uint actionId, AggregatedEvent ev, ISet<string> used)
    {
        var baseId = $"auto_cast_{actionId:x}";
        if (used.Add(baseId))
        {
            return baseId;
        }

        var suffix = SanitizeId(ev.Key.Source ?? ev.Key.Name ?? "source");
        var candidate = $"{baseId}_{suffix}";
        var index = 2;
        while (!used.Add(candidate))
        {
            candidate = $"{baseId}_{suffix}_{index++}";
        }

        return candidate;
    }

    private static string SanitizeId(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
            else if (sb.Length == 0 || sb[^1] != '_')
            {
                sb.Append('_');
            }
        }

        var result = sb.ToString().Trim('_');
        return string.IsNullOrEmpty(result) ? "source" : result;
    }

    public sealed record GenerationResult(
        IReadOnlyList<TriggerDefinition> Generated,
        IReadOnlyList<string> Skipped);
}
