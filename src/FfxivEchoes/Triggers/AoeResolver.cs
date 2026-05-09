using System;
using Dalamud.Plugin.Services;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace FfxivEchoes.Triggers;

/// <summary>
/// Lumina Action データから AoE 形状（半径と CastType）を解決するヘルパ。
/// AutoTelegraphService と PredictedCastReminderService で共有する。
/// </summary>
public static class AoeResolver
{
    public sealed record AoeInfo(float Radius, int CastType, bool FromCaster, uint OmenId = 0);

    /// <summary>
    /// Lumina Action から AoE 情報を取得。AoE でない場合や異常値は null。
    /// </summary>
    public static AoeInfo? Resolve(IDataManager dataManager, uint actionId, IPluginLog? log = null)
    {
        if (actionId == 0) return null;
        try
        {
            var sheet = dataManager.GetExcelSheet<LuminaAction>();
            if (!sheet.TryGetRow(actionId, out var row))
            {
                return null;
            }
            var effectRange = (float)row.EffectRange;
            if (effectRange <= 0)
            {
                log?.Debug("[FfxivEchoes] AoE skip non-AoE id={Id:X4} range={R}m castType={Ct}",
                    actionId, effectRange, (int)row.CastType);
                return null;
            }
            // 100m 超のみ無視（FFXIV のアリーナはほぼ 50m 以内、それ超は誤データ）
            if (effectRange > 100f)
            {
                log?.Debug("[FfxivEchoes] AoE skip oversized id={Id:X4} range={R}m", actionId, effectRange);
                return null;
            }

            var castType = (int)row.CastType;
            // 2/5: target/caster centered circle
            // 3: cone, 4: line  → caster-anchored
            // 6/7/10/11/12/13: donut / cross / various special shapes → caster-anchored
            var fromCaster = castType == 5 || castType == 3 || castType == 4 ||
                             castType == 6 || castType == 7 || castType == 10 ||
                             castType == 11 || castType == 12 || castType == 13;
            // Omen ID（テレグラフのアセット参照）を取得。失敗時は 0
            uint omenId = 0;
            try
            {
                omenId = row.Omen.RowId;
            }
            catch { /* Omen フィールドが取れない場合は無視 */ }

            log?.Debug("[FfxivEchoes] AoE resolve id={Id:X4} range={R}m castType={Ct} omen={Om}",
                actionId, effectRange, castType, omenId);
            return new AoeInfo(effectRange, castType, fromCaster, omenId);
        }
        catch (Exception ex)
        {
            log?.Warning(ex, "[FfxivEchoes] AoE resolve 失敗 id={Id}", actionId);
            return null;
        }
    }

    /// <summary>
    /// 既知の Omen ID から gimmick を推測。
    /// 未知の Omen ID は CastType ベースのフォールバックを呼び出し側で。
    /// </summary>
    public static string? GuessGimmickByOmen(uint omenId)
    {
        // FFXIV の Omen 一覧の代表値（経験的に蓄積したもの）。
        // 完全網羅は不可能だが、よく使われるテレグラフをカバーする。
        return omenId switch
        {
            // Donut（中央安置 / 外周危険）系
            53 or 60 or 61 => "outer_ring",
            // 通常円（中央危険 / 外周安置）系
            1 or 2 or 3 or 4 or 5 => "inner_circle",
            // 標準コーン
            10 or 11 or 12 or 13 or 14 => "cone",
            // 直線
            20 or 21 or 22 or 23 => "cone",
            // 半円（half-plane 的）
            40 or 41 or 42 => "half_plane",
            _ => null, // 未知は呼び出し側で判定
        };
    }

    /// <summary>
    /// CastType と EffectRange から arena_view 用の gimmick タイプを推測。
    /// アリーナ図に表示するための形状。
    /// </summary>
    public static string GuessGimmick(int castType, float effectRange)
    {
        return castType switch
        {
            // ターゲット/キャスター中心円: 中央が危険で外周が安置。
            2 => "inner_circle",
            5 => "inner_circle",
            // Donut: 外周が危険で内側が安置。
            6 => "outer_ring",
            // Cone / Line：cone gimmick（demo の扇形コーンと同じ）
            3 => "cone",
            4 => "cone",
            _ => "inner_circle",
        };
    }

    public static bool TryParseCastId(string spec, out uint id)
    {
        id = 0;
        if (string.IsNullOrEmpty(spec)) return false;
        var s = spec;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (s.StartsWith("#")) s = s[1..];
        return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out id);
    }
}
