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
    private readonly MinimapWindow _minimap;
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
        MinimapWindow minimap,
        TriggerStore store,
        IPluginLog log)
    {
        _dataManager = dataManager;
        _objectTable = objectTable;
        _worldOverlay = worldOverlay;
        _minimap = minimap;
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
        if (file is null)
        {
            _log.Information("[FfxivEchoes] AutoTelegraph: skip (zone={Zone}, no file)", _currentZone);
            return;
        }
        if (!file.AutoSettings.ShowAutoTelegraphs)
        {
            _log.Information("[FfxivEchoes] AutoTelegraph: skip (show_auto_telegraphs=false)");
            return;
        }

        if (IsFriendlyActor(ev.SourceId))
        {
            _log.Information("[FfxivEchoes] AutoTelegraph: skip friendly source ({Name})", ev.SourceName);
            return;
        }

        var aoe = AoeResolver.Resolve(_dataManager, ev.CastActionId, _log);
        if (aoe is null)
        {
            _log.Information("[FfxivEchoes] AutoTelegraph: skip non-AoE cast {Name} (id={Id:X4})",
                ev.CastActionName, ev.CastActionId);
            return;
        }
        var radius = aoe.Radius;
        var shape = "circle";
        var inferredFromCaster = aoe.FromCaster;

        Vector3? worldPos = null;
        var src = _objectTable.SearchById(ev.SourceId);
        if (src is not null)
        {
            worldPos = new Vector3(src.Position.X, src.Position.Y, src.Position.Z);
        }
        if (!inferredFromCaster && ev.TargetId is { } tid && tid != 0)
        {
            var target = _objectTable.SearchById(tid);
            if (target is not null)
            {
                worldPos = new Vector3(target.Position.X, target.Position.Y, target.Position.Z);
            }
        }
        if (worldPos is null)
        {
            _log.Warning("[FfxivEchoes] AutoTelegraph: 位置不明 cast={Name} src={SrcId} tgt={TgtId}",
                ev.CastActionName, ev.SourceId, ev.TargetId ?? 0);
            return;
        }

        _log.Information("[FfxivEchoes] AutoTelegraph: 描画 cast={Name} radius={R}m shape={S} pos=({X:0.0},{Z:0.0})",
            ev.CastActionName, radius, shape, worldPos.Value.X, worldPos.Value.Z);

        // 1. ミニマップ（俯瞰アリーナ図）に確定ギミック表示
        var gimmick = AoeResolver.GuessGimmick(aoe.CastType, aoe.Radius);
        _minimap.AddArenaView(
            gimmick: gimmick,
            callout: $"確定：{ev.CastActionName}",
            durationSec: ev.CastTime + 0.5,
            direction: gimmick == "cone" ? "N" : null,
            fanDeg: gimmick == "cone" ? 90 : null,
            arenaRadius: 20.0);

        // 2. フィールドにも実体半径の円マーカー（show_auto_telegraphs が ON なら）
        _worldOverlay.AddMarker(
            worldPos: worldPos.Value,
            shape: shape,
            radius: radius,
            colorHex: "#FF6464",
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
            // Lumina の EffectRange が異常に大きい一部の特殊アクション
            // （アリーナ全域 80m など）は描画しない
            if (effectRange > 50f)
            {
                _log.Debug("[FfxivEchoes] AutoTelegraph: skip oversized AoE id={Id:X4} range={R}m",
                    actionId, effectRange);
                return false;
            }

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
