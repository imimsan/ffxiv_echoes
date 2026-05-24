using System;
using System.Collections.Generic;
using System.Linq;
using FfxivEchoes.Recording;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Triggers;

/// <summary>
/// 録画集計から PT 攻略登録用の下書きメカニクスを生成する。
/// </summary>
public static class StrategyDraftGenerator
{
    public sealed record GenerationResult(
        IReadOnlyList<MechanicStrategy> Generated,
        IReadOnlyList<string> Skipped);

    /// <summary>1 戦あたり何回までなら「ギミック性のあるオブジェクト」とみなすか。
    /// 環境オブジェクト（脱出地点・秘紋・装飾物）は 1 戦中に何度も出現するのでこれで弾く。</summary>
    private const double ObjectAppearMaxPerBattle = 5.0;

    /// <summary>
    /// 候補ラベルが PC ジョブ由来かどうかを判定（pattern match）。
    /// 実装は <see cref="FfxivEchoes.Recording.PcSkillNameFilter.LooksLikePcSkillOrPet"/> に委譲。
    /// 旧 API 互換のため public に残し、テストや外部からも参照可能。
    /// </summary>
    public static bool LooksLikePcSkillOrPet(string? label, string eventType)
        => FfxivEchoes.Recording.PcSkillNameFilter.LooksLikePcSkillOrPet(label, eventType);

    public static GenerationResult Generate(
        AggregatedEvents aggregate,
        StrategyProfile profile,
        IReadOnlySet<string>? partyMembers = null,
        int maxDrafts = 80)
    {
        var generated = new List<MechanicStrategy>();
        var skipped = new List<string>();

        var battleCount = Math.Max(1, aggregate.BattleCount);
        var predictions = RecordingPredictionPlanner.BuildTimelinePredictions(
                aggregate,
                partyMembers: partyMembers,
                includeStatusGains: true,
                includeStatusUpdates: true,
                includeHpChanges: true,
                includeObjects: true)
            .Where(p => IsUsefulDraftCandidate(p, battleCount))
            .OrderBy(p => p.RelativeSeconds)
            .ThenBy(p => p.EventType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Label, StringComparer.OrdinalIgnoreCase)
            .Take(maxDrafts)
            .ToArray();

        var existingKeys = new HashSet<string>(
            profile.Mechanics.Select(BuildExistingKey).OfType<string>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var prediction in predictions)
        {
            var exactKey = BuildPredictionKey(prediction, includeSource: true);
            var genericKey = BuildPredictionKey(prediction, includeSource: false);
            if (existingKeys.Contains(exactKey) || existingKeys.Contains(genericKey))
            {
                skipped.Add($"{prediction.Label} ({prediction.EventType}) は既存下書きと重複");
                continue;
            }

            var draft = StrategyPlanResolver.CreateMechanicDraft(
                prediction,
                MakeUniqueId(profile, prediction),
                profile);
            generated.Add(draft);
            existingKeys.Add(exactKey);
            if (string.IsNullOrWhiteSpace(prediction.Source))
            {
                existingKeys.Add(genericKey);
            }
        }

        return new GenerationResult(generated, skipped);
    }

    private static bool IsUsefulDraftCandidate(RecordingTimelinePrediction prediction, int battleCount)
    {
        // PC ジョブスキル / ペット名は除外（party filter が漏らした場合の最終防衛）。
        // 「ハンマーコンボ実行可」「カーバンクル」等が攻略 mechanic 下書きに混入するのを防ぐ。
        if (LooksLikePcSkillOrPet(prediction.Label, prediction.EventType))
        {
            return false;
        }

        // object_appear / hp_change は mechanic 自動生成から完全除外する。
        // 旧実装は「1 戦あたり ≤5 件」のオブジェクトを採用していたが、Bozja / Eureka 系
        // コンテンツでは「ベヒーモス」「ケツァクワァトル」「ビュトン」「脱出地点」「秘紋」
        // 等のオブジェクト名がそのまま mechanic 化されてタイムラインを汚染する。
        //
        // hp_change：ラベルが source 名（=ボス名）になるため「ゾディアーク」「コキュートス」
        // 等のボス名がタイムラインに連続表示される問題が発生。「フェーズ移行 80% で何を回避」
        // という情報を本来欲しいが、ラベルが「ゾディアーク」だけでは意味不明。
        // 必要なユーザーは手動で hp_change トリガー mechanic を作って意味のあるラベルを付ける。
        if (prediction.EventType == "object_appear" || prediction.EventType == "hp_change")
        {
            return false;
        }
        // status_update は除外：status_gain と重複した情報で、しかも source_id が無く
        // 「自己付与かどうか」も判定不可。リフレッシュタイマーごとに発火するので
        // 数値 ID（"2624" 等）の漏れの主要因。status_gain が拾うべき情報を全て持っている。
        return prediction.EventType is
            "cast_start" or
            "status_gain";
    }

    private static string? BuildExistingKey(MechanicStrategy mechanic)
    {
        var occurrence = mechanic.OccurrenceIndex ?? 0;
        if (mechanic.AttachedTo is { } match)
        {
            if (!string.IsNullOrWhiteSpace(match.CastId))
            {
                return AppendSource($"cast_start|{match.CastId}|{occurrence}", match.Source);
            }

            if (match.StatusId is { } statusId)
            {
                return AppendSource($"{mechanic.SourceEventType ?? "status_gain"}|{statusId}|{occurrence}", match.Source);
            }

            if (!string.IsNullOrWhiteSpace(match.Actor))
            {
                return $"{mechanic.SourceEventType ?? "object_appear"}|{match.Actor}|{occurrence}";
            }
        }

        if (!string.IsNullOrWhiteSpace(mechanic.SourceEventType) &&
            !string.IsNullOrWhiteSpace(mechanic.Label))
        {
            return $"{mechanic.SourceEventType}|{mechanic.Label}|{occurrence}";
        }

        return null;
    }

    private static string BuildPredictionKey(RecordingTimelinePrediction prediction, bool includeSource)
    {
        var idOrLabel = string.IsNullOrWhiteSpace(prediction.Id)
            ? prediction.Label
            : prediction.Id;
        var key = $"{prediction.EventType}|{idOrLabel}|{prediction.OccurrenceIndex}";
        return includeSource ? AppendSource(key, prediction.Source) : key;
    }

    private static string AppendSource(string key, string? source)
    {
        return string.IsNullOrWhiteSpace(source)
            ? key
            : $"{key}|src:{source.Trim()}";
    }

    private static string MakeUniqueId(StrategyProfile profile, RecordingTimelinePrediction prediction)
    {
        var raw = $"{prediction.EventType}_{(string.IsNullOrWhiteSpace(prediction.Id) ? prediction.Label : prediction.Id)}_{prediction.OccurrenceIndex + 1}";
        var baseId = Sanitize(raw);
        var id = baseId;
        var n = 1;
        while (profile.Mechanics.Any(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            n++;
            id = $"{baseId}_{n}";
        }

        return id;
    }

    private static string Sanitize(string value)
    {
        var chars = value
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray();
        var normalized = new string(chars).Trim('_');
        while (normalized.Contains("__", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("__", "_", StringComparison.Ordinal);
        }

        return string.IsNullOrWhiteSpace(normalized) ? "learned_mechanic" : normalized;
    }
}
