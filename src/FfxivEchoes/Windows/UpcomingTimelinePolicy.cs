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

    /// <summary>直近表示の未来側上限（秒）。バー全長 BarWindowSec=30 の少し先まで見せつつ、
    /// それ以上遠い予測は出さない。フェーズ注釈の無いタイムラインでは、後半技が録画集計で
    /// 早い相対秒の別 occurrence として混ざることがあり、上限が無いと前半戦闘中に遠い後半項目が
    /// 直近バーへ漏れる。上限でこの漏れを抑える（フェーズ注釈があればフェーズ絞り込みが優先）。</summary>
    public const double DisplayHorizonSec = 45.0;

    public static bool ShouldDisplayUpcomingItem(double itemTime, double nowRel)
    {
        return itemTime > nowRel && itemTime <= nowRel + DisplayHorizonSec;
    }

    /// <summary>
    /// 実戦で観測したボス cast から、どのプルセグメント（前半/後半 等）に居るかを判定する。
    /// 一致したら <paramref name="resolvedFirstCastId"/> にそのセグメントの判定キャスト ID を返す。
    /// </summary>
    /// <remarks>
    /// 判定順: (1) 観測 cast がいずれかのセグメントの判定キャスト(FirstCastId)に一致 →確定。
    /// (2) 排他フォールバック: 観測 cast が「ただ 1 つのセグメントにしか存在しない cast_id」なら
    /// そのセグメントに確定（開幕 cast を取りこぼしてもログ欠落耐性で確定できる）。
    /// PC（パーティメンバー）の詠唱は判定に使わない（ボスのギミック cast のみで分岐を決める）。
    /// </remarks>
    public static bool TryResolveSegment(
        IReadOnlyList<RecordingSegment> segments,
        uint observedCastId,
        ISet<string> partyMembers,
        string? sourceName,
        out string resolvedFirstCastId)
    {
        resolvedFirstCastId = string.Empty;
        if (segments is null || segments.Count < 2 || observedCastId == 0)
        {
            return false;
        }
        if (!string.IsNullOrEmpty(sourceName) && partyMembers is not null && partyMembers.Contains(sourceName))
        {
            return false;
        }

        // (1) 判定キャスト一致。
        foreach (var segment in segments)
        {
            if (AoeResolver.TryParseCastId(segment.FirstCastId, out var firstId) && firstId == observedCastId)
            {
                resolvedFirstCastId = segment.FirstCastId;
                return true;
            }
        }

        // (2) 排他フォールバック: 観測 cast が単一セグメント固有なら確定。
        string? hit = null;
        var matchCount = 0;
        foreach (var segment in segments)
        {
            foreach (var idStr in segment.CastIds)
            {
                if (AoeResolver.TryParseCastId(idStr, out var cid) && cid == observedCastId)
                {
                    matchCount++;
                    hit = segment.FirstCastId;
                    break;
                }
            }
        }
        if (matchCount == 1 && hit is not null)
        {
            resolvedFirstCastId = hit;
            return true;
        }

        return false;
    }

    /// <summary>
    /// プルセグメント確定状態に応じて、タイムライン生成に使う集計を選ぶ。
    /// セグメント未分離（&lt; 2）なら従来の全合算（他コンテンツ後方互換）、
    /// 確定済みなら該当セグメントのみ、未確定なら共通技のみ（前半/後半の取り違えを防ぐ安全縮退）。
    /// </summary>
    public static AggregatedEvents SelectActiveAgg(SegmentedAggregate segmented, string? activeSegmentFirstCastId)
    {
        if (segmented is null)
        {
            return new AggregatedEvents(System.Array.Empty<AggregatedEvent>(), 0, 0, 0);
        }
        if (segmented.Segments.Count < 2)
        {
            return segmented.Combined;
        }
        if (!string.IsNullOrEmpty(activeSegmentFirstCastId))
        {
            foreach (var segment in segmented.Segments)
            {
                if (string.Equals(segment.FirstCastId, activeSegmentFirstCastId, StringComparison.OrdinalIgnoreCase))
                {
                    return segment.Events;
                }
            }
        }
        return segmented.CommonAgg;
    }

    /// <summary>直近予測が空のときの表示文言（本当に録画が無い場合）。</summary>
    public const string EmptyNoRecordings = "予測データなし — 録画してから 1 戦してください";

    /// <summary>プルセグメント分離済みだが未確定（開幕の最初のボス技待ち）のときの文言。
    /// 前半/後半の取り違えを防ぐため確定まで非表示にしているだけで、録画は十分にある。</summary>
    public const string EmptyWaitingForBossCast = "最初のボス技を待っています…（開幕の判定中）";

    /// <summary>録画はあるが直近窓に予測が無いときの中立文言。「録画しろ」と誤誘導しない。</summary>
    public const string EmptyNoUpcoming = "この先しばらく直近の予測はありません";

    /// <summary>
    /// 直近予測が空のとき、文脈に応じた表示文言を返す。録画済みのユーザーに「録画してから1戦」と
    /// 誤誘導しないための出し分け。
    /// </summary>
    /// <param name="segmentCount">プルセグメント数（&gt;=2 で前半/後半等に自動分離済み）。</param>
    /// <param name="segmentResolved">実戦の最初のボス cast でセグメントが確定済みか。</param>
    /// <param name="hasRecordedEvents">このゾーンに集計可能な録画イベントが存在するか。</param>
    public static string ResolveEmptyStateMessage(int segmentCount, bool segmentResolved, bool hasRecordedEvents)
    {
        // 複数セグメント検出済みで未確定 = 最初のボス技待ち（取り違え防止で確定まで非表示）。
        if (segmentCount >= 2 && !segmentResolved)
        {
            return EmptyWaitingForBossCast;
        }
        // 録画はあるが直近窓に予測が無いだけ（戦闘終盤・間隔が空く区間 等）。誤誘導を避ける。
        if (hasRecordedEvents)
        {
            return EmptyNoUpcoming;
        }
        // 本当に録画ゼロ。
        return EmptyNoRecordings;
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
