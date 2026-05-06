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
