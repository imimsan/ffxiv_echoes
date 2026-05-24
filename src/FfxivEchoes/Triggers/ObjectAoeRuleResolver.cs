using System;
using System.Collections.Generic;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Triggers;

public sealed record ObjectAoeRuleMatch(
    ObjectAoeRule Rule,
    StrategyAoeZone Zone,
    string ShapeNote,
    string Source);

public static class ObjectAoeRuleResolver
{
    public static bool TryResolve(
        TriggerFile? file,
        uint dataId,
        string? objectName,
        AutoAoeArenaConfig arena,
        out ObjectAoeRuleMatch match)
    {
        match = default!;
        var profile = file is null ? null : StrategyPlanResolver.SelectActiveProfile(file);
        if (profile?.ObjectAoeRules is null || profile.ObjectAoeRules.Count == 0)
        {
            return false;
        }

        foreach (var rule in profile.ObjectAoeRules)
        {
            if (!Matches(rule, dataId, objectName))
            {
                continue;
            }

            if (!ShouldUseObjectAoeRule(file, rule))
            {
                continue;
            }

            var zone = BuildZone(rule, arena);
            match = new ObjectAoeRuleMatch(
                rule,
                zone,
                ShapeNoteFor(zone.Shape),
                string.IsNullOrWhiteSpace(rule.Source) ? "manual" : rule.Source!);
            return true;
        }

        return false;
    }

    public static bool Matches(ObjectAoeRule rule, uint dataId, string? objectName)
    {
        if (!rule.Enabled)
        {
            return false;
        }

        return MatchesIgnoringEnabled(rule, dataId, objectName);
    }

    /// <summary>
    /// <see cref="Matches"/> から enabled チェックを外したバージョン。「重複ルール検出」のように
    /// ユーザーが無効化済の同名ルールも『存在する』扱いにしたい場合に使う。
    /// </summary>
    /// <remarks>
    /// PersistLearnedObjectRule の重複防止で誤って <see cref="Matches"/> を使うと、ユーザーが
    /// 「ケツァクウァトル」のルールを無効化しても、recording_action 学習のたびに新しい
    /// active ルールが追加されてしまう（→ ユーザー体感「無効化したのに AoE がまた出る」）。
    /// </remarks>
    public static bool MatchesIgnoringEnabled(ObjectAoeRule rule, uint dataId, string? objectName)
    {
        var hasDataId = rule.DataId is { } ruleDataId && ruleDataId != 0;
        var hasName = !string.IsNullOrWhiteSpace(rule.ObjectName);
        if (!hasDataId && !hasName)
        {
            return false;
        }

        if (hasDataId && rule.DataId!.Value != dataId)
        {
            return false;
        }

        if (!hasName)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(objectName))
        {
            return false;
        }

        var pattern = rule.ObjectName!.Trim();
        var name = objectName.Trim();
        var normalizedPattern = NormalizeObjectNameForMatch(pattern);
        var normalizedName = NormalizeObjectNameForMatch(name);
        return (rule.NameMatch ?? "contains").Trim().ToLowerInvariant() switch
        {
            "exact" => string.Equals(normalizedName, normalizedPattern, StringComparison.OrdinalIgnoreCase),
            "startswith" => normalizedName.StartsWith(normalizedPattern, StringComparison.OrdinalIgnoreCase),
            _ => normalizedName.Contains(normalizedPattern, StringComparison.OrdinalIgnoreCase),
        };
    }

    public static bool ShouldUseObjectAoeRule(TriggerFile? file, ObjectAoeRule rule)
    {
        return ShouldPersistLearnedObjectRule(file, rule.ObjectName, rule.Source);
    }

    public static bool ShouldPersistLearnedObjectRule(
        TriggerFile? file,
        string? objectName,
        string? source)
    {
        if (!IsRecordingActionSource(source))
        {
            return true;
        }

        return !IsKnownEventSourceName(file, objectName);
    }

    public static bool IsKnownEventSourceName(TriggerFile? file, string? objectName)
    {
        if (file is null || string.IsNullOrWhiteSpace(objectName))
        {
            return false;
        }

        foreach (var sourceName in EnumerateEventSourceNames(file))
        {
            if (NamesReferToSameActor(objectName, sourceName))
            {
                return true;
            }
        }

        return false;
    }

    public static StrategyAoeZone BuildZone(ObjectAoeRule rule, AutoAoeArenaConfig arena)
    {
        var shape = NormalizeShape(rule.Shape);
        var radius = Math.Max(0.5, rule.RadiusM);
        var zone = new StrategyAoeZone
        {
            Id = string.IsNullOrWhiteSpace(rule.Id) ? MakeRuleId(rule.DataId ?? 0, rule.ObjectName) : rule.Id,
            Label = string.IsNullOrWhiteSpace(rule.Label) ? rule.ObjectName : rule.Label,
            Shape = shape,
            Anchor = "each_matched_object",
            RadiusM = radius,
            Color = string.IsNullOrWhiteSpace(rule.Color) ? "#FF6464" : rule.Color,
            IsDanger = true,
            LiveFloorPaint = rule.LiveFloorPaint,
            SuppressAutoAoe = true,
        };

        if (shape == "donut")
        {
            zone.InnerRadiusM = rule.InnerRadiusM is > 0
                ? rule.InnerRadiusM
                : Math.Max(0.5, radius * 0.30);
        }
        else if (shape is "cone" or "donut_cone")
        {
            zone.FanDeg = rule.FanDeg is > 0 ? rule.FanDeg : 90.0;
            if (shape == "donut_cone")
            {
                zone.InnerRadiusM = rule.InnerRadiusM is > 0 ? rule.InnerRadiusM : Math.Max(0.5, radius * 0.30);
            }
        }

        if (shape is "rect" or "line")
        {
            zone.HalfWidthM = rule.HalfWidthM is > 0
                ? rule.HalfWidthM
                : AoeGeometryPolicy.DefaultLineHalfWidthM;
        }
        else if (shape == "half_plane")
        {
            zone.HalfWidthM = rule.HalfWidthM is > 0
                ? rule.HalfWidthM
                : Math.Max(arena.ArenaRadius, AoeGeometryPolicy.DefaultLineHalfWidthM);
            zone.RadiusM = Math.Max(radius, arena.ArenaRadius * 2.0);
        }

        return zone;
    }

    public static ObjectAoeRule CreateLearnedRule(
        uint dataId,
        string? objectName,
        StrategyAoeZone zone,
        string source)
    {
        return new ObjectAoeRule
        {
            Id = MakeRuleId(dataId, objectName),
            Enabled = true,
            ObjectName = string.IsNullOrWhiteSpace(objectName) ? null : objectName.Trim(),
            NameMatch = string.IsNullOrWhiteSpace(objectName) ? "exact" : "contains",
            // 同名オブジェクトで内部 DataId が個体ごとに揺れるギミックがあるため、
            // 名前が取れている場合は名前一致を優先し、DataId は保存しない。
            DataId = string.IsNullOrWhiteSpace(objectName) && dataId != 0 ? dataId : null,
            Label = zone.Label,
            Source = source,
            Shape = NormalizeShape(zone.Shape),
            RadiusM = Math.Max(0.5, zone.RadiusM),
            InnerRadiusM = zone.InnerRadiusM,
            FanDeg = zone.FanDeg,
            HalfWidthM = zone.HalfWidthM,
            DurationSec = zone.DurationSec,
            Color = zone.Color,
            LiveFloorPaint = zone.LiveFloorPaint,
        };
    }

    public static StrategyAoeZone BuildZoneFromKnownGeometry(
        KnownAoeGeometrySpec spec,
        string? label)
    {
        var zone = new StrategyAoeZone
        {
            Id = string.Empty,
            Label = label,
            Shape = NormalizeShape(spec.Shape),
            Anchor = "each_matched_object",
            RadiusM = Math.Max(0.5f, spec.RadiusM),
            InnerRadiusM = spec.InnerRadiusM > 0 ? spec.InnerRadiusM : null,
            FanDeg = spec.FanDeg > 0 ? spec.FanDeg : null,
            HalfWidthM = spec.HalfWidthM > 0 ? spec.HalfWidthM : null,
            Color = "#FF6464",
            IsDanger = true,
            LiveFloorPaint = true,
            SuppressAutoAoe = true,
        };
        if (zone.Shape is "rect" or "line" && zone.HalfWidthM is null)
        {
            zone.HalfWidthM = AoeGeometryPolicy.DefaultLineHalfWidthM;
        }
        return zone;
    }

    public static string ShapeNoteFor(string? shape)
    {
        return NormalizeShape(shape) switch
        {
            "donut" => "ドーナツ",
            "cone" => "扇形",
            "rect" or "line" => "直線",
            "cross" => "十字",
            "half_plane" => "半面",
            "donut_cone" => "扇ドーナツ",
            _ => "円",
        };
    }

    public static string NormalizeShape(string? shape)
    {
        return (shape ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "donut" => "donut",
            "cone" => "cone",
            "rect" => "rect",
            "line" => "line",
            "cross" => "cross",
            "halfplane" or "half_plane" => "half_plane",
            "donutcone" or "donut_cone" => "donut_cone",
            _ => "circle",
        };
    }

    private static string NormalizeObjectNameForMatch(string value)
    {
        return value.Trim()
            // ゲーム内表記/手入力/録画由来で揺れやすい表記。パラデイグマ系 object AoE の
            // 学習ルールが「ケツァクウァトル」でも実オブジェクト「ケツァクワァトル」に当たるようにする。
            .Replace("ウァ", "ワァ", StringComparison.Ordinal);
    }

    private static bool IsRecordingActionSource(string? source)
    {
        return string.Equals(source?.Trim(), "recording_action", StringComparison.OrdinalIgnoreCase);
    }

    private static bool NamesReferToSameActor(string left, string? right)
    {
        if (string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        var a = NormalizeObjectNameForMatch(left);
        var b = NormalizeObjectNameForMatch(right);
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
               a.Contains(b, StringComparison.OrdinalIgnoreCase) ||
               b.Contains(a, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string?> EnumerateEventSourceNames(TriggerFile file)
    {
        foreach (var marker in file.RaidWideMarkers)
        {
            yield return marker.Source;
        }

        foreach (var trigger in file.Triggers)
        {
            yield return trigger.Match?.Source;
        }

        foreach (var note in file.Notes)
        {
            yield return note.AttachedTo?.Source;
        }

        foreach (var profile in file.StrategyProfiles)
        {
            foreach (var mechanic in profile.Mechanics)
            {
                yield return mechanic.AttachedTo?.Source;
                foreach (var trigger in mechanic.Triggers)
                {
                    yield return trigger.Match?.Source;
                }
            }
        }
    }

    public static string MakeRuleId(uint dataId, string? objectName)
    {
        var hash = StableHash(objectName ?? string.Empty);
        return dataId == 0
            ? $"obj_aoe_{hash:X8}"
            : $"obj_aoe_{dataId:X}_{hash:X8}";
    }

    private static uint StableHash(string value)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var ch in value)
            {
                hash ^= ch;
                hash *= 16777619u;
            }
            return hash;
        }
    }
}
