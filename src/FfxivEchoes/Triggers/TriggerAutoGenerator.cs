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
        return Generate(agg, existing, partyMembers, AutoAoeDisplayPolicy.ResolveArena(null), null);
    }

    public GenerationResult Generate(
        AggregatedEvents agg,
        TriggerFile file,
        IReadOnlyList<string>? partyMembers = null)
    {
        return Generate(agg, file.Triggers, partyMembers, AutoAoeDisplayPolicy.ResolveArena(file), file);
    }

    private GenerationResult Generate(
        AggregatedEvents agg,
        IReadOnlyList<TriggerDefinition> existing,
        IReadOnlyList<string>? partyMembers,
        AutoAoeArenaConfig arena,
        TriggerFile? file)
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

        // 同じ cast_id を 1 回だけ生成するための集合。
        // 集計側 EventKey が (cast_id, source, target) で分裂するようになったので、
        // 同 cast_id 異 source の AggregatedEvent が複数あると、ここで dedup しないと
        // auto_cast_6c60 / auto_cast_6c60_source / auto_cast_6c60_source_2 ... のように
        // ほぼ同内容の trigger が大量に生まれる。観測回数が最も多いものを代表として採用する。
        var bestPerCastId = new Dictionary<string, AggregatedEvent>(StringComparer.OrdinalIgnoreCase);
        foreach (var ev in agg.Events)
        {
            if (ev.Key.Type != "cast_start") continue;
            if (string.IsNullOrEmpty(ev.Key.Id)) continue;
            if (!string.IsNullOrEmpty(ev.Key.Source) && partySet.Contains(ev.Key.Source))
            {
                continue;
            }
            if (!bestPerCastId.TryGetValue(ev.Key.Id, out var prev) || ev.Count > prev.Count)
            {
                bestPerCastId[ev.Key.Id] = ev;
            }
        }

        // skipped 表示用：同じ cast_id / 同じ name は 1 行にまとめる（既存ファイルに
        // 過去の重複生成が大量に残っていると、ここに「コキュートス (existing)」が
        // 何十個も並んで読みにくくなる）
        var skippedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ev in bestPerCastId.Values)
        {
            if (existingCastIds.Contains(ev.Key.Id!))
            {
                var label = ev.Key.Name ?? ev.Key.Id ?? "?";
                if (skippedNames.Add(label))
                {
                    skipped.Add($"{label} (existing)");
                }
                continue;
            }

            var trigger = BuildTrigger(ev, generatedIds, arena, file);
            if (trigger is not null)
            {
                generated.Add(trigger);
                existingCastIds.Add(ev.Key.Id!); // 念のため：同じ cast_id を二度生成しない
            }
        }

        return new GenerationResult(generated, skipped);
    }

    private TriggerDefinition? BuildTrigger(
        AggregatedEvent ev,
        ISet<string> generatedIds,
        AutoAoeArenaConfig arena,
        TriggerFile? file)
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

        if (AutoSafeCallPlanner.IsRaidWide(file, actionId, castName))
        {
            return trigger;
        }

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
                ArenaRadius = arena.ArenaRadius,
                ArenaShape = arena.ArenaShape,
                ArenaWidth = arena.ArenaWidth,
                ArenaDepth = arena.ArenaDepth,
                ArenaCenterX = arena.LockedArenaCenter?.X,
                ArenaCenterZ = arena.LockedArenaCenter?.Z,
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
