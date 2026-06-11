using System;
using System.Collections.Generic;

namespace FfxivEchoes.Windows;

/// <summary>
/// 俯瞰図への AoE 事前描画（予測レイヤ）の純粋ロジック。
/// Dalamud 型に依存しない（テスト可能）。
/// </summary>
public static class PredictedAoePreviewPolicy
{
    /// <summary>同時に事前描画する最大件数。視覚ノイズとフレームコストの上限。</summary>
    public const int MaxPreviewItems = 4;

    /// <summary>実 cast_start 観測後、この秒数以内の同 cast_id 予測は確定描画へ譲る。</summary>
    public const double ConfirmSuppressWindowSec = 6.0;

    /// <summary>
    /// 補正済み upcoming リスト（時刻昇順前提）から事前描画候補を選ぶ。
    /// cast_start かつ Id あり、残り 0 &lt; t ≤ advanceSec のものを最大 maxItems 件。
    /// 同一 cast_id+source は最早 1 件に集約（同名別 cast_id ＝真偽の両候補は両方残す）。
    /// </summary>
    public static void SelectPreviewCandidates(
        IReadOnlyList<UpcomingItem> items,
        double nowRel,
        double advanceSec,
        int maxItems,
        List<UpcomingItem> output)
    {
        output.Clear();
        if (advanceSec <= 0 || maxItems <= 0)
        {
            return;
        }

        foreach (var item in items)
        {
            if (output.Count >= maxItems)
            {
                break;
            }
            if (!string.Equals(item.EventType, "cast_start", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (string.IsNullOrEmpty(item.Id))
            {
                continue;
            }
            var remaining = item.Time - nowRel;
            if (remaining <= 0 || remaining > advanceSec)
            {
                continue;
            }
            if (ContainsSameCast(output, item))
            {
                continue;
            }
            output.Add(item);
        }
    }

    private static bool ContainsSameCast(List<UpcomingItem> selected, UpcomingItem item)
    {
        foreach (var s in selected)
        {
            if (string.Equals(s.Id, item.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.Source ?? string.Empty, item.Source ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 円形（CastType 2/5）25m 以上・その他 30m 以上は回避不能の全体攻撃扱いで事前描画しない。
    /// </summary>
    public static bool ShouldSkipOversized(int castType, float radiusM)
        => (castType is 2 or 5 && radiusM >= 25f) || radiusM >= 30f;

    /// <summary>
    /// 実 cast_start を観測済み（confirmedAtRel）の予測は ±windowSec の間、事前描画を止めて
    /// 既存の確定描画（AutoTelegraphService）に譲る。次回出現（窓外）は再び事前描画する。
    /// </summary>
    public static bool IsSuppressedByConfirmedCast(
        double itemTimeRel,
        double? confirmedAtRel,
        double windowSec = ConfirmSuppressWindowSec)
        => confirmedAtRel is { } c && Math.Abs(itemTimeRel - c) <= windowSec;
}
