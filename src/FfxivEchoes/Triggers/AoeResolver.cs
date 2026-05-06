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
    public sealed record AoeInfo(float Radius, int CastType, bool FromCaster);

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
            if (effectRange <= 0) return null;
            // 50m 超は描画しない（アリーナ全域系）
            if (effectRange > 50f)
            {
                log?.Debug("[FfxivEchoes] AoE skip oversized id={Id:X4} range={R}m", actionId, effectRange);
                return null;
            }

            var castType = (int)row.CastType;
            var fromCaster = castType == 5 || castType == 3 || castType == 4 || castType == 6;
            return new AoeInfo(effectRange, castType, fromCaster);
        }
        catch (Exception ex)
        {
            log?.Warning(ex, "[FfxivEchoes] AoE resolve 失敗 id={Id}", actionId);
            return null;
        }
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
