using FfxivEchoes.Recording;

namespace FfxivEchoes.Windows;

public static class UpcomingTimelinePolicy
{
    private const string CommonGroupName = "共通";

    public static IReadOnlyList<RecordingTimelinePrediction> FilterDisplayPredictions(
        IEnumerable<RecordingTimelinePrediction> predictions)
    {
        return predictions
            .Where(prediction => !IsRawUnknownActionLabel(prediction.Label))
            .ToArray();
    }

    public static bool ShouldDeduplicateLabelPair(string? firstEventType, string? secondEventType)
    {
        return !IsAutoAttack(firstEventType) && !IsAutoAttack(secondEventType);
    }

    public static bool ShouldDeduplicateDisplayItem(
        string? firstEventType,
        string? secondEventType,
        string? firstLabel,
        string? secondLabel,
        string? firstSource,
        string? secondSource)
    {
        if (!ShouldDeduplicateLabelPair(firstEventType, secondEventType))
        {
            return false;
        }
        var a = SourceGroupName(firstSource, firstLabel);
        var b = SourceGroupName(secondSource, secondLabel);

        var normalizedFirst = FormatRowLabel(firstLabel, firstSource);
        var normalizedSecond = FormatRowLabel(secondLabel, secondSource);
        if (!string.Equals(normalizedFirst, normalizedSecond, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 攻略登録から来る共通 note と、録画予測から来る「ボス名付き cast_start」は
        // 同じ技を二重表示しているだけ。具体的なボス名がある側を残す。
        return IsCommonGroup(a) || IsCommonGroup(b);
    }

    public static string SourceGroupName(string? source, string? label = null)
    {
        if (!string.IsNullOrWhiteSpace(source))
        {
            return source.Trim();
        }

        return TrySplitSourcePrefix(label, out var inferred, out _)
            ? inferred
            : CommonGroupName;
    }

    public static bool ShouldDrawSourceGroupHeader(string? source, string? label = null)
    {
        return !IsCommonGroup(SourceGroupName(source, label));
    }

    public static bool ShouldDisplayUpcomingItem(double itemTime, double nowRel)
    {
        return itemTime > nowRel;
    }

    public static string FormatRowLabel(string? label, string? source)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return string.Empty;
        }

        var text = label.Trim();
        var group = source?.Trim();
        if (!string.IsNullOrEmpty(group))
        {
            var colon = group + ":";
            var wideColon = group + "：";
            if (text.StartsWith(colon, StringComparison.OrdinalIgnoreCase))
            {
                return text[colon.Length..].Trim();
            }
            if (text.StartsWith(wideColon, StringComparison.OrdinalIgnoreCase))
            {
                return text[wideColon.Length..].Trim();
            }
        }

        if (TrySplitSourcePrefix(text, out _, out var row))
        {
            return row;
        }

        return text;
    }

    private static bool TrySplitSourcePrefix(string? label, out string source, out string row)
    {
        source = string.Empty;
        row = string.Empty;
        if (string.IsNullOrWhiteSpace(label))
        {
            return false;
        }

        var text = label.Trim();
        var index = text.IndexOf(':');
        if (index < 0)
        {
            index = text.IndexOf('：');
        }

        if (index <= 0 || index >= text.Length - 1)
        {
            return false;
        }

        source = text[..index].Trim();
        row = text[(index + 1)..].Trim();
        return source.Length > 0 && row.Length > 0;
    }

    public static bool IsCommonGroup(string? sourceGroup)
    {
        return string.Equals(
            sourceGroup,
            CommonGroupName,
            StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAutoAttack(string? eventType)
    {
        return string.Equals(eventType, "auto_attack", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsRawUnknownActionLabel(string? label)
    {
        return !string.IsNullOrEmpty(label) &&
               label.StartsWith("Action#", StringComparison.OrdinalIgnoreCase);
    }
}
