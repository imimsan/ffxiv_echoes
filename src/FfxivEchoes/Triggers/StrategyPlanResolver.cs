using System;
using System.Collections.Generic;
using System.Linq;
using FfxivEchoes.Recording;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Triggers;

public static class StrategyPlanResolver
{
    public static StrategyProfile? SelectActiveProfile(TriggerFile file)
    {
        if (file.StrategyProfiles.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(file.ActiveStrategyProfileId))
        {
            var active = file.StrategyProfiles.FirstOrDefault(p =>
                p.Enabled &&
                string.Equals(p.Id, file.ActiveStrategyProfileId, StringComparison.OrdinalIgnoreCase));
            if (active is not null)
            {
                return active;
            }
        }

        return file.StrategyProfiles.FirstOrDefault(p => p.Enabled);
    }

    public static IReadOnlyList<TimelineNote> BuildTimelineNotes(TriggerFile file)
    {
        var profile = SelectActiveProfile(file);
        if (profile is null)
        {
            return Array.Empty<TimelineNote>();
        }

        return profile.Mechanics
            .Where(m => m.Enabled)
            .Select(m => BuildTimelineNote(profile, m))
            .ToArray();
    }

    public static TimelineNote BuildTimelineNote(StrategyProfile profile, MechanicStrategy mechanic)
    {
        return new TimelineNote
        {
            Id = $"strategy_{profile.Id}_{mechanic.Id}",
            Time = mechanic.Time ?? 0,
            Label = string.IsNullOrEmpty(mechanic.Label) ? mechanic.Id : mechanic.Label,
            Duration = mechanic.Duration,
            Role = mechanic.Role,
            Job = mechanic.Job,
            Color = mechanic.Color ?? "#F472B6",
            AttachedTo = mechanic.AttachedTo,
            Icons = new List<string> { "S" },
            AdvanceWarningSec = mechanic.AdvanceWarningSec,
            WarningText = mechanic.WarningText ?? mechanic.Callout ?? mechanic.Label,
        };
    }

    public static IReadOnlyList<ActionDefinition> BuildReminderActions(
        StrategyProfile profile,
        MechanicStrategy mechanic)
    {
        var actions = new List<ActionDefinition>();
        var text = mechanic.WarningText ?? mechanic.Callout ?? mechanic.Label;
        if (!string.IsNullOrWhiteSpace(text))
        {
            actions.Add(new ActionDefinition
            {
                Type = "tts",
                Text = text,
            });
        }

        var positions = SelectPositions(profile, mechanic);
        if (!string.IsNullOrWhiteSpace(mechanic.Gimmick) ||
            positions.Count > 0 ||
            mechanic.SafeZone is not null)
        {
            actions.Add(new ActionDefinition
            {
                Type = "arena_view",
                Gimmick = string.IsNullOrWhiteSpace(mechanic.Gimmick) ? "scatter" : mechanic.Gimmick,
                Callout = mechanic.Callout ?? mechanic.WarningText ?? mechanic.Label,
                Duration = mechanic.Duration ?? 5.0,
                ArenaRadius = profile.ArenaRadius,
                SafeZone = mechanic.SafeZone,
                StrategyProfileId = profile.Id,
                MechanicId = mechanic.Id,
                StrategyPositions = positions,
            });
        }

        return actions;
    }

    public static (StrategyProfile? Profile, MechanicStrategy? Mechanic) FindMechanicForPrediction(
        TriggerFile file,
        RecordingPrediction prediction,
        double maxTimeDeltaSeconds = 15.0)
    {
        var profile = SelectActiveProfile(file);
        if (profile is null)
        {
            return (null, null);
        }

        var mechanic = FindBestMechanic(
            profile,
            prediction.CastId,
            prediction.Label,
            prediction.RelativeSeconds,
            maxTimeDeltaSeconds);
        return mechanic is null ? (profile, null) : (profile, mechanic);
    }

    public static (StrategyProfile? Profile, MechanicStrategy? Mechanic) FindMechanicForCast(
        TriggerFile file,
        uint castActionId,
        string castActionName,
        double? relativeSeconds = null,
        double maxTimeDeltaSeconds = 15.0)
    {
        var profile = SelectActiveProfile(file);
        if (profile is null)
        {
            return (null, null);
        }

        var mechanic = FindBestMechanic(
            profile,
            $"0x{castActionId:X}",
            castActionName,
            relativeSeconds,
            maxTimeDeltaSeconds);
        return mechanic is null ? (profile, null) : (profile, mechanic);
    }

    public static MechanicStrategy CreateMechanicDraft(RecordingTimelinePrediction prediction, string id)
    {
        return new MechanicStrategy
        {
            Id = id,
            Label = prediction.Label,
            Time = prediction.RelativeSeconds,
            AdvanceWarningSec = 5.0,
            WarningText = prediction.Label,
            Callout = prediction.Label,
            AttachedTo = BuildMatch(prediction),
            Color = prediction.Confidence >= 0.75 ? "#F472B6" : "#FBBF24",
            SourceEventType = prediction.EventType,
            ObservedCount = prediction.ObservedCount,
            OccurrenceSeenCount = prediction.OccurrenceSeenCount,
            Confidence = prediction.Confidence,
            TimeJitterSeconds = prediction.TimeJitterSeconds,
        };
    }

    private static MatchCondition? BuildMatch(RecordingTimelinePrediction prediction)
    {
        return prediction.EventType switch
        {
            "cast_start" => new MatchCondition
            {
                CastId = EmptyToNull(prediction.Id),
                CastName = prediction.Label,
                Source = EmptyToNull(prediction.Source),
            },
            "action_used" => new MatchCondition
            {
                ActionId = EmptyToNull(prediction.Id),
                ActionName = prediction.Label,
                Source = EmptyToNull(prediction.Source),
            },
            "status_gain" or "status_update" => new MatchCondition
            {
                StatusId = uint.TryParse(prediction.Id, out var statusId) ? statusId : null,
                StatusName = prediction.Label,
                Target = string.IsNullOrWhiteSpace(prediction.Target)
                    ? null
                    : new TargetSpec(new List<string> { prediction.Target }),
            },
            "object_appear" or "object_disappear" => new MatchCondition
            {
                Actor = prediction.Label,
            },
            _ => null,
        };
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static MechanicStrategy? FindBestMechanic(
        StrategyProfile profile,
        string castId,
        string castName,
        double? relativeSeconds,
        double maxTimeDeltaSeconds)
    {
        MechanicStrategy? best = null;
        var bestDistance = double.MaxValue;

        foreach (var mechanic in profile.Mechanics)
        {
            if (!mechanic.Enabled || mechanic.AttachedTo is not { } match)
            {
                continue;
            }

            if (!MatchesCast(match, castId, castName))
            {
                continue;
            }

            var distance = relativeSeconds is not null && mechanic.Time is not null
                ? Math.Abs(mechanic.Time.Value - relativeSeconds.Value)
                : 0;
            if (relativeSeconds is not null &&
                mechanic.Time is not null &&
                distance > maxTimeDeltaSeconds)
            {
                continue;
            }

            if (distance < bestDistance)
            {
                best = mechanic;
                bestDistance = distance;
            }
        }

        return best;
    }

    private static bool MatchesCast(MatchCondition match, string castId, string castName)
    {
        if (!string.IsNullOrEmpty(match.CastId) &&
            !CastIdsEqual(match.CastId, castId))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(match.CastName) &&
            !string.Equals(match.CastName, castName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !string.IsNullOrEmpty(match.CastId) || !string.IsNullOrEmpty(match.CastName);
    }

    private static bool CastIdsEqual(string left, string right)
    {
        return TryParseHexId(left, out var leftId) && TryParseHexId(right, out var rightId)
            ? leftId == rightId
            : string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseHexId(string value, out uint id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        return uint.TryParse(
            text,
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture,
            out id);
    }

    private static List<StrategyPosition> SelectPositions(StrategyProfile profile, MechanicStrategy mechanic)
    {
        if (mechanic.Positions.Count == 0)
        {
            return string.Equals(mechanic.Gimmick, "scatter", StringComparison.OrdinalIgnoreCase)
                ? profile.SpreadPositions.ToList()
                : new List<StrategyPosition>();
        }

        var wanted = new HashSet<string>(mechanic.Positions, StringComparer.OrdinalIgnoreCase);
        return profile.SpreadPositions
            .Where(p => !string.IsNullOrEmpty(p.Slot) && wanted.Contains(p.Slot))
            .ToList();
    }
}
