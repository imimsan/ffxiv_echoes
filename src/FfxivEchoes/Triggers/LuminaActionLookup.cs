using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace FfxivEchoes.Triggers;

/// <summary>
/// 本番用の <see cref="IActionLookup"/> 実装。Dalamud の <c>IDataManager</c> 経由で
/// Lumina の Action sheet を引く。シートロードは初回に 1 度だけ、以後は actionId 単位で
/// <see cref="ActionGeometry"/> を on-demand キャッシュする。
/// </summary>
public sealed class LuminaActionLookup : IActionLookup
{
    private readonly IDataManager _dataManager;
    private readonly IPluginLog? _log;
    private readonly ConcurrentDictionary<uint, ActionGeometry?> _cache = new();

    public LuminaActionLookup(IDataManager dataManager, IPluginLog? log = null)
    {
        _dataManager = dataManager;
        _log = log;
    }

    public ActionGeometry? TryGet(uint actionId)
    {
        if (actionId == 0) return null;
        if (_cache.TryGetValue(actionId, out var cached)) return cached;

        ActionGeometry? result = null;
        try
        {
            var sheet = _dataManager.GetExcelSheet<LuminaAction>();
            if (sheet.TryGetRow(actionId, out var row))
            {
                result = BuildFromRow(actionId, row);
            }
        }
        catch (Exception ex)
        {
            _log?.Warning(ex, "[FfxivEchoes] LuminaActionLookup: TryGet 失敗 id={Id:X4}", actionId);
        }
        _cache[actionId] = result;
        return result;
    }

    public IEnumerable<ActionGeometry> ListAll()
    {
        Lumina.Excel.ExcelSheet<LuminaAction>? sheet = null;
        try
        {
            sheet = _dataManager.GetExcelSheet<LuminaAction>();
        }
        catch (Exception ex)
        {
            _log?.Warning(ex, "[FfxivEchoes] LuminaActionLookup: ListAll で sheet 取得失敗");
            yield break;
        }

        foreach (var row in sheet)
        {
            ActionGeometry? geom = null;
            try
            {
                geom = BuildFromRow(row.RowId, row);
            }
            catch
            {
                // 1 行壊れていても全体列挙を止めない
            }
            if (geom is not null) yield return geom;
        }
    }

    private static ActionGeometry BuildFromRow(uint actionId, LuminaAction row)
    {
        var name = SafeReadName(row);
        var castType = (int)row.CastType;
        var effectRange = (float)row.EffectRange;
        var xAxisMod = SafeReadXAxisModifier(row);
        var omenId = SafeReadOmenId(row);
        var castTimeMs = SafeReadCastTimeMs(row);
        var isPlayerAction = SafeReadIsPlayerAction(row);
        return new ActionGeometry(
            Id: actionId,
            Name: name,
            CastType: castType,
            EffectRangeM: effectRange,
            XAxisModifierM: xAxisMod,
            OmenId: omenId,
            CastTimeMs: castTimeMs,
            IsPlayerAction: isPlayerAction);
    }

    private static bool SafeReadIsPlayerAction(LuminaAction row)
    {
        try { return row.IsPlayerAction; }
        catch { return false; }
    }

    private static string SafeReadName(LuminaAction row)
    {
        try { return row.Name.ToString() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static uint SafeReadOmenId(LuminaAction row)
    {
        try { return row.Omen.RowId; }
        catch { return 0u; }
    }

    private static float SafeReadXAxisModifier(LuminaAction row)
    {
        // 一部 Lumina バージョンで XAxisModifier が欠ける可能性に備えてフォールバック。
        try { return (float)row.XAxisModifier; }
        catch { return 0f; }
    }

    private static int SafeReadCastTimeMs(LuminaAction row)
    {
        // Lumina の Cast100ms は 0.1 秒単位。0 = インスタント。
        try { return (int)row.Cast100ms * 100; }
        catch { return 0; }
    }
}
