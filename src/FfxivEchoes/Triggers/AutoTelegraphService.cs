using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using FfxivEchoes.Capture;
using FfxivEchoes.Events;
using FfxivEchoes.Triggers.Models;
using FfxivEchoes.Windows;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace FfxivEchoes.Triggers;

/// <summary>
/// ボスのキャストに対し、Lumina Action データから AoE 形状を自動推測して
/// WorldOverlayWindow にフィールドマーカー（円形）を描画する。
/// </summary>
/// <remarks>
/// 簡易版：CastType が 2（Circle）/ 5（PBAoE on caster）/ 6（Donut）の場合に
/// EffectRange を半径として、ソース（ボス）の位置に円を描く。
/// Cone（3）/ Line（4）の方向計算は未対応で、半径だけ目安として描く。
/// auto_settings.show_auto_telegraphs が true のときだけ動作。
/// PT メンバー / 自分のキャストは無視（敵のみ対象）。
/// </remarks>
public sealed class AutoTelegraphService : IDisposable
{
    private readonly IDataManager _dataManager;
    private readonly IObjectTable _objectTable;
    private readonly WorldOverlayWindow _worldOverlay;
    private readonly TriggerStore _store;
    private readonly IPluginLog _log;

    private readonly IDisposable _castStartSub;
    private readonly IDisposable _zoneSub;

    private string _currentZone = "Unknown";

    public AutoTelegraphService(
        IEventBus bus,
        IDataManager dataManager,
        IObjectTable objectTable,
        WorldOverlayWindow worldOverlay,
        TriggerStore store,
        IPluginLog log)
    {
        _dataManager = dataManager;
        _objectTable = objectTable;
        _worldOverlay = worldOverlay;
        _store = store;
        _log = log;

        _castStartSub = bus.Subscribe<CastStartedEvent>(OnCastStart);
        _zoneSub = bus.Subscribe<ZoneChangedEvent>(z =>
            _currentZone = string.IsNullOrEmpty(z.ZoneName) ? "Unknown" : z.ZoneName);
    }

    public void Dispose()
    {
        _castStartSub.Dispose();
        _zoneSub.Dispose();
    }

    private void OnCastStart(CastStartedEvent ev)
    {
        var file = _store.GetByZone(_currentZone);
        if (file is null || !file.AutoSettings.ShowAutoTelegraphs)
        {
            return;
        }

        // ソースが PT 内なら無視（敵のみ対象）
        if (IsFriendlyActor(ev.SourceId))
        {
            return;
        }

        if (!TryResolveAoe(ev.CastActionId, out var radius, out var shape, out var inferredFromCaster))
        {
            return;
        }

        // ソース（敵）の位置を取得。inferredFromCaster=true なら敵中心、
        // false でもターゲット位置が無ければとりあえず敵中心に出す。
        Vector3? worldPos = null;
        var src = _objectTable.SearchById(ev.SourceId);
        if (src is not null)
        {
            worldPos = new Vector3(src.Position.X, src.Position.Y, src.Position.Z);
        }
        // 自分中心 / 一部のターゲット指定 AoE はターゲット位置に出す
        if (!inferredFromCaster && ev.TargetId is { } tid && tid != 0)
        {
            var target = _objectTable.SearchById(tid);
            if (target is not null)
            {
                worldPos = new Vector3(target.Position.X, target.Position.Y, target.Position.Z);
            }
        }
        if (worldPos is null) return;

        // キャスト時間 + 0.5 秒で消す（実発動と少し被らせて視認性を保つ）
        _worldOverlay.AddMarker(
            worldPos: worldPos.Value,
            shape: shape,
            radius: radius,
            colorHex: "#FF6464", // 薄い赤
            durationSec: ev.CastTime + 0.5);
    }

    private bool IsFriendlyActor(uint id)
    {
        var obj = _objectTable.SearchById(id);
        if (obj is null) return false;
        // ObjectKind: Pc=プレイヤー、BattleNpc=敵/NPC など。
        // PC（プレイヤーキャラ）なら友軍とみなしてスキップ。
        return obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Pc;
    }

    /// <summary>
    /// Lumina Action から AoE 形状を推測。circle / donut / square のどれかを返す。
    /// 推測できなければ false。
    /// </summary>
    private bool TryResolveAoe(uint actionId, out float radius, out string shape, out bool fromCaster)
    {
        radius = 0;
        shape = "circle";
        fromCaster = false;
        if (actionId == 0) return false;

        try
        {
            var sheet = _dataManager.GetExcelSheet<LuminaAction>();
            if (!sheet.TryGetRow(actionId, out var row))
            {
                return false;
            }
            var effectRange = (float)row.EffectRange;
            if (effectRange <= 0) return false;

            // CastType: 1=ST, 2=Circle (target-centered), 3=Cone, 4=Line,
            //           5=PBAoE on caster, 6=Donut, 7+=L/Cross 等の特殊
            var castType = (int)row.CastType;
            switch (castType)
            {
                case 2:
                    radius = effectRange;
                    shape = "circle";
                    fromCaster = false; // ターゲット中心 or 着弾点中心
                    return true;
                case 5:
                    radius = effectRange;
                    shape = "circle";
                    fromCaster = true; // キャスター中心
                    return true;
                case 6:
                    radius = effectRange;
                    shape = "circle"; // donut も外径だけ円で描く（簡易）
                    fromCaster = true;
                    return true;
                case 3: // cone（方向不明なのでとりあえず半径だけ円で示す）
                case 4: // line（同上）
                    radius = effectRange;
                    shape = "circle";
                    fromCaster = true;
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[FfxivEchoes] AutoTelegraph: Lumina Action 解決失敗 (id={Id})", actionId);
            return false;
        }
    }
}
