using System;
using System.Collections.Generic;
using FfxivEchoes.Recording;
using FfxivEchoes.Triggers;
using FfxivEchoes.Triggers.Models;

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

    /// <summary>
    /// 録画予測（cast_start 等）から、分岐 (TimelineBranch) で棄却された攻撃を除外する。
    /// 攻撃A/B のうち実際に来なかった方の予測キャストがタイムラインに残るのを防ぐ。
    /// </summary>
    /// <remarks>
    /// 録画予測 (<see cref="RecordingTimelinePrediction"/>) には branch_id が無いため、
    /// アクティブプロファイルの mechanic を AttachedTo.CastId で逆引きして branch_id を解決する。
    /// あるキャストに紐づく branch のうち少なくとも 1 つが active/common なら表示する
    /// （= 同一キャストが複数分岐に割り当てられていても、生きている分岐があれば残す）。
    /// 分岐に紐づかない共通キャストはそのまま通す。
    /// </remarks>
    /// <param name="branchActiveCheck">
    /// 通常は <see cref="BranchObserverService.IsActiveOrCommon"/>。true なら表示。
    /// </param>
    public static IReadOnlyList<RecordingTimelinePrediction> FilterBranchRejectedPredictions(
        IReadOnlyList<RecordingTimelinePrediction> predictions,
        TriggerFile file,
        Func<string?, bool> branchActiveCheck)
    {
        var profile = StrategyPlanResolver.SelectActiveProfile(file);
        if (profile is null)
        {
            return predictions;
        }

        var castIdToBranchIds = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        void Register(string? castId, string branchId)
        {
            if (string.IsNullOrEmpty(castId)) return;
            if (!castIdToBranchIds.TryGetValue(castId, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                castIdToBranchIds[castId] = set;
            }
            set.Add(branchId);
        }

        foreach (var mech in profile.Mechanics)
        {
            if (!mech.Enabled || string.IsNullOrEmpty(mech.BranchId)) continue;
            // 旧パス（AttachedTo）と新パス（Triggers の cast / action_used）の両方の cast_id を逆引きに登録する。
            Register(mech.AttachedTo?.CastId, mech.BranchId!);
            foreach (var trig in mech.Triggers)
            {
                if (string.Equals(trig.Type, "cast", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(trig.Type, "action_used", StringComparison.OrdinalIgnoreCase))
                {
                    Register(trig.Match?.CastId, mech.BranchId!);
                }
            }
        }

        if (castIdToBranchIds.Count == 0)
        {
            return predictions;
        }

        var result = new List<RecordingTimelinePrediction>(predictions.Count);
        foreach (var prediction in predictions)
        {
            if (string.IsNullOrEmpty(prediction.Id) ||
                !castIdToBranchIds.TryGetValue(prediction.Id, out var branchIds))
            {
                // 分岐に紐づかない（共通）キャストは常に表示
                result.Add(prediction);
                continue;
            }

            var anyActive = false;
            foreach (var branchId in branchIds)
            {
                if (branchActiveCheck(branchId))
                {
                    anyActive = true;
                    break;
                }
            }
            if (anyActive)
            {
                result.Add(prediction);
            }
        }

        return result;
    }

    /// <summary>
    /// 録画予測（cast_start 等）から、過去フェーズに属する攻撃を除外する。
    /// 録画予測には phase 注釈が無いため、<see cref="FilterBranchRejectedPredictions"/> と同じ手法で
    /// アクティブプロファイルの mechanic を AttachedTo.CastId / trigger.Match.CastId で逆引きして
    /// mechanic.Phase を解決する。あるキャストに紐づく phase のうち少なくとも 1 つが active なら表示する
    /// （= 同一 cast_id が複数フェーズに登場しても、現在以降のフェーズに属するなら残す）。
    /// phase に紐づかないキャストはそのまま通す（後方互換・安全側）。
    /// </summary>
    /// <param name="phaseActiveCheck">
    /// 通常は <see cref="CurrentPhaseTracker.IsPhaseActive"/>。true なら表示。
    /// </param>
    public static IReadOnlyList<RecordingTimelinePrediction> FilterPastPhasePredictions(
        IReadOnlyList<RecordingTimelinePrediction> predictions,
        TriggerFile file,
        Func<string?, bool> phaseActiveCheck)
    {
        var profile = StrategyPlanResolver.SelectActiveProfile(file);
        if (profile is null)
        {
            return predictions;
        }

        var castIdToPhases = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        void Register(string? castId, string phase)
        {
            if (string.IsNullOrEmpty(castId)) return;
            if (!castIdToPhases.TryGetValue(castId, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                castIdToPhases[castId] = set;
            }
            set.Add(phase);
        }

        foreach (var mech in profile.Mechanics)
        {
            if (!mech.Enabled || string.IsNullOrEmpty(mech.Phase)) continue;
            Register(mech.AttachedTo?.CastId, mech.Phase!);
            foreach (var trig in mech.Triggers)
            {
                if (string.Equals(trig.Type, "cast", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(trig.Type, "action_used", StringComparison.OrdinalIgnoreCase))
                {
                    Register(trig.Match?.CastId, mech.Phase!);
                }
            }
        }

        if (castIdToPhases.Count == 0)
        {
            return predictions;
        }

        var result = new List<RecordingTimelinePrediction>(predictions.Count);
        foreach (var prediction in predictions)
        {
            if (string.IsNullOrEmpty(prediction.Id) ||
                !castIdToPhases.TryGetValue(prediction.Id, out var phases))
            {
                // phase に紐づかないキャストは常に表示（安全側）
                result.Add(prediction);
                continue;
            }

            var anyActive = false;
            foreach (var phase in phases)
            {
                if (phaseActiveCheck(phase))
                {
                    anyActive = true;
                    break;
                }
            }
            if (anyActive)
            {
                result.Add(prediction);
            }
        }

        return result;
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
        if (string.IsNullOrEmpty(label))
        {
            return false;
        }
        if (label.StartsWith("Action#", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        // 名称未解決の cast_id がそのまま漏れた "0x2B34" 形式のラベルもジャンク扱いにする
        // （例: cast_id 11060 が Action#11060 → "0x2B34" のままタイムライン/読み上げに出る）。
        return IsRawHexLabel(label);
    }

    private static bool IsRawHexLabel(string label)
    {
        if (label.Length < 3 || label[0] != '0' || (label[1] != 'x' && label[1] != 'X'))
        {
            return false;
        }
        for (var i = 2; i < label.Length; i++)
        {
            if (!Uri.IsHexDigit(label[i]))
            {
                return false;
            }
        }
        return true;
    }
}
