using System;
using System.Collections.Generic;
using System.Linq;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Triggers;

/// <summary>
/// フェーズ移行判定の純粋ロジック。Dalamud 型に依存しないため単体テスト可能（CS0012 回避）。
/// </summary>
public static class PhaseTransitionPolicy
{
    /// <summary>
    /// ボス HP%（0-100）が <paramref name="threshold"/> を上から下へ跨いだかを判定する。
    /// <paramref name="prev"/> が NaN（初回観測）の場合は false。
    /// MechanicTriggerService.OnHpChanged の crossedBelow と同値。
    /// </summary>
    public static bool CrossedBelow(float prev, float now, float threshold)
    {
        // prev=NaN のとき NaN >= threshold は false になるため、初回観測は自然に false。
        return prev >= threshold && now < threshold;
    }

    /// <summary>
    /// フェーズ名 → 序数（1 始まり）のマップを構築する。序数はフェーズが入場する順序。
    /// 境界は (1) phase 付き sync_point（expected_time 昇順）→ (2) phase 付き hp_pct トリガー
    /// （hp_pct_below 降順）の順に番号付けする。最初の出現を優先（重複は無視）。
    /// </summary>
    /// <remarks>
    /// マップに現れないフェーズ（初期フェーズや phase 未注釈の mechanic）は序数 0 として扱う。
    /// よって「最初のフェーズ以外は sync_point か hp_pct トリガーに phase を付ける」必要がある。
    /// マップが空＝フェーズ未注釈のコンテンツでは <see cref="IsPastPhase"/> が常に false を返し、
    /// フェーズ絞り込みは無効化される（完全な後方互換）。
    /// </remarks>
    public static IReadOnlyDictionary<string, int> BuildPhaseOrdinals(TriggerFile file)
    {
        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var sp in file.SyncPoints
                     .Where(s => !string.IsNullOrEmpty(s.Phase))
                     .OrderBy(s => s.ExpectedTime))
        {
            if (seen.Add(sp.Phase!))
            {
                order.Add(sp.Phase!);
            }
        }

        var hpBoundaries = new List<(double Hp, string Phase)>();
        foreach (var profile in file.StrategyProfiles)
        {
            if (!profile.Enabled)
            {
                continue;
            }
            // フェーズ順序の構築はフェーズ名の存在確認であり、mechanic の有効/無効とは独立。
            // disabled な mechanic が定義する phase 名も順序に含めないと、それを参照する別の
            // enabled mechanic が ordinal=0 と誤解決され IsPastPhase=true で永久非表示になる（RES-09）。
            foreach (var mech in profile.Mechanics)
            {
                foreach (var trig in mech.Triggers)
                {
                    if (string.Equals(trig.Type, "hp_pct", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrEmpty(trig.Phase) &&
                        trig.HpPctBelow is { } below)
                    {
                        hpBoundaries.Add((below, trig.Phase!));
                    }
                }
            }
        }

        foreach (var (_, phase) in hpBoundaries.OrderByDescending(b => b.Hp))
        {
            if (seen.Add(phase))
            {
                order.Add(phase);
            }
        }

        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < order.Count; i++)
        {
            map[order[i]] = i + 1;
        }
        return map;
    }

    /// <summary>
    /// フェーズ名の序数を返す。マップに無い（初期フェーズ / phase 未注釈）なら 0。
    /// </summary>
    public static int ResolvePhaseOrdinal(string? phaseId, IReadOnlyDictionary<string, int> phaseOrdinals)
    {
        return !string.IsNullOrEmpty(phaseId) && phaseOrdinals.TryGetValue(phaseId, out var ord)
            ? ord
            : 0;
    }

    /// <summary>
    /// <paramref name="mechanicPhase"/> が現在フェーズ（<paramref name="currentOrdinal"/>）より
    /// 前なら true（= もう過ぎたので非表示・読み上げ抑制すべき）。
    /// フェーズ未注釈（マップ空）のコンテンツでは常に false（絞り込み無効・後方互換）。
    /// </summary>
    public static bool IsPastPhase(
        string? mechanicPhase,
        int currentOrdinal,
        IReadOnlyDictionary<string, int> phaseOrdinals)
    {
        if (phaseOrdinals.Count == 0)
        {
            return false;
        }
        // phase 未設定（共通ギミック）は branch_id=null と同様、フェーズに関係なく常に表示する。
        // フェーズ絞り込みの対象は「明示的に phase を付けた mechanic」のみ。
        if (string.IsNullOrEmpty(mechanicPhase))
        {
            return false;
        }
        return ResolvePhaseOrdinal(mechanicPhase, phaseOrdinals) < currentOrdinal;
    }
}
