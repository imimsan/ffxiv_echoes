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
    private readonly IEventBus _bus;
    private readonly IPluginLog _log;

    private readonly IDisposable _castStartSub;
    private readonly IDisposable _actionSub;
    private readonly IDisposable _zoneSub;

    private string _currentZone = "Unknown";

    // 同時多発キャストの重複表示防止：(cast_id, source_id) ペアごとに最後の発火時刻を覚える。
    // 同じ cast_id でもソースが違う（左翼 vs 右翼）場合は別物として両方描画する。
    private readonly Dictionary<(uint CastId, uint SourceId), DateTimeOffset> _lastDrawnAt = new();
    private const double DedupWindowSeconds = 0.3;

    public AutoTelegraphService(
        IEventBus bus,
        IDataManager dataManager,
        IObjectTable objectTable,
        WorldOverlayWindow worldOverlay,
        MinimapWindow minimap,
        TriggerStore store,
        IPluginLog log)
    {
        _bus = bus;
        _dataManager = dataManager;
        _objectTable = objectTable;
        _worldOverlay = worldOverlay;
        _minimap = minimap;
        _store = store;
        _log = log;

        _castStartSub = bus.Subscribe<CastStartedEvent>(OnCastStart);
        _actionSub = bus.Subscribe<ActionUsedEvent>(OnActionUsed);
        _zoneSub = bus.Subscribe<ZoneChangedEvent>(z =>
            _currentZone = string.IsNullOrEmpty(z.ZoneName) ? "Unknown" : z.ZoneName);
    }

    public void Dispose()
    {
        _castStartSub.Dispose();
        _actionSub.Dispose();
        _zoneSub.Dispose();
    }

    private void OnCastStart(CastStartedEvent ev)
    {
        // 注意：ゾーンファイルが無くても AoE 描画は行う。極ゾディアーク等で
        // 初見ゾーンに突入したばかりのユーザーでも警告を出すため。
        var file = _store.GetByZone(_currentZone);

        var isFriendly = IsFriendlyActor(ev.SourceId);
        if (isFriendly)
        {
            _log.Information("[FfxivEchoes] AutoTelegraph: skip friendly source ({Name})", ev.SourceName);
            return;
        }

        // 同時多発の dedup：(cast_id, source_id) ペアで判定。
        // 同 cast_id でもソースが違うなら（左翼 vs 右翼）別物として両方描画。
        var now = ev.Timestamp;
        var key = (ev.CastActionId, ev.SourceId);
        if (_lastDrawnAt.TryGetValue(key, out var prev) &&
            (now - prev).TotalSeconds < DedupWindowSeconds)
        {
            _log.Debug("[FfxivEchoes] AutoTelegraph: skip duplicate cast id={Id:X4} src={Src} within {Sec}s",
                ev.CastActionId, ev.SourceId, DedupWindowSeconds);
            return;
        }
        _lastDrawnAt[key] = now;

        if (file is not null && file.AutoSettings.EnableTriggers)
        {
            var strategy = StrategyPlanResolver.FindMechanicForCast(file, ev.CastActionId, ev.CastActionName);
            if (strategy.Profile is not null && strategy.Mechanic is not null)
            {
                var actions = StrategyPlanResolver.BuildReminderActions(strategy.Profile, strategy.Mechanic);
                if (actions.Count > 0)
                {
                    _bus.Publish(new TriggerFiredEvent(
                        Timestamp: DateTimeOffset.UtcNow,
                        Zone: _currentZone,
                        TriggerId: $"__strategy_{strategy.Profile.Id}_{strategy.Mechanic.Id}",
                        TriggerName: strategy.Mechanic.Label,
                        Actions: actions,
                        SourceEvent: ev));
                    return;
                }
            }
        }

        var aoe = AoeResolver.Resolve(_dataManager, ev.CastActionId, _log);
        var namedSafeCall = AutoSafeCallPlanner.CreateKnown(ev.CastActionId, ev.CastActionName);
        // ゾーン file が無い場合は default の auto_settings を使う（show_auto_telegraphs=true）
        var autoSettings = file?.AutoSettings ?? new AutoSettings { ShowAutoTelegraphs = true };
        var decision = AttackDisplayPolicy.Decide(
            autoSettings,
            new AttackDisplayRequest(
                IsFriendly: false,
                HasAoe: aoe is not null || namedSafeCall is not null,
                IsAutoAttack: false,
                IsCast: true));
        if (decision == AttackDisplayDecision.None)
        {
            _log.Information("[FfxivEchoes] AutoTelegraph: skip cast {Name} (decision=none)", ev.CastActionName);
            return;
        }

        var src = _objectTable.SearchById(ev.SourceId);
        var sourceWorld = src is null
            ? (Vector3?)null
            : new Vector3(src.Position.X, src.Position.Y, src.Position.Z);

        if (aoe is null && namedSafeCall is null)
        {
            DrawAttackPulse(ev.CastActionName, ev.CastTime, sourceWorld, "Cast");
            return;
        }

        if (aoe is null && namedSafeCall is not null)
        {
            var knownFacingAngleRad = ArenaProjection.UsesFacing(namedSafeCall.Gimmick) && src is not null
                ? ArenaProjection.RotationToMapAngleRad(src.Rotation)
                : (float?)null;

            _minimap.AddArenaView(
                gimmick: namedSafeCall.Gimmick,
                callout: namedSafeCall.Callout,
                durationSec: ev.CastTime + 0.5,
                direction: ArenaProjection.UsesFacing(namedSafeCall.Gimmick) ? "N" : null,
                fanDeg: namedSafeCall.FanDeg,
                arenaRadius: 20.0,
                directionAngleRad: knownFacingAngleRad,
                sourceWorld: sourceWorld);
            PublishAutoSafeCall(ev, namedSafeCall);
            return;
        }

        var radius = aoe!.Radius;
        var shape = "circle";
        var inferredFromCaster = aoe.FromCaster;

        var worldPos = sourceWorld;
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

        // 1. ミニマップ（俯瞰アリーナ図）に確定/推定ギミック表示
        var safeCall = namedSafeCall ?? AutoSafeCallPlanner.Create(aoe, ev.CastActionName);
        var visualCall = safeCall ?? AutoSafeCallPlanner.CreateVisual(aoe, ev.CastActionName);
        // cone / half_plane はソース（ボス）の rotation から実方向を計算
        float? facingAngleRad = null;
        if (ArenaProjection.UsesFacing(visualCall?.Gimmick) && src is not null)
        {
            // FFXIV: rotation 0 = +Z (south) 方向。ミニマップ render の atan2 系で
            // south = π/2 になるよう変換：renderAngle = bossRot + π/2 - π/2 = bossRot
            // と思いきや、render の cone 描画は (cos, sin) を使うため、
            // 直接 rotation を渡すと south=0 が east になってしまう。
            // 正しい変換: render angle = π/2 - bossRotation
            facingAngleRad = ArenaProjection.RotationToMapAngleRad(src.Rotation);
        }
        if (visualCall is not null)
        {
            // sourceWorld には「AoE が実際に発動する場所」を渡す。
            // - キャスター中心 (CastType=5/3/4/6) → ボス位置
            // - ターゲット中心 (CastType=2) → ターゲット位置（worldPos が既にそれ）
            _minimap.AddArenaView(
                gimmick: visualCall.Gimmick,
                callout: visualCall.Callout,
                durationSec: ev.CastTime + 0.5,
                direction: ArenaProjection.UsesFacing(visualCall.Gimmick) ? "N" : null,
                fanDeg: visualCall.FanDeg,
                arenaRadius: 20.0,
                directionAngleRad: facingAngleRad,
                sourceWorld: worldPos,
                aoeRadius: aoe?.Radius,
                aoeCastType: aoe?.CastType);
        }

        if (safeCall is not null)
        {
            PublishAutoSafeCall(ev, safeCall);
        }
    }

    private void OnActionUsed(ActionUsedEvent ev)
    {
        var file = _store.GetByZone(_currentZone);
        if (file is null)
        {
            return;
        }

        var isFriendly = IsFriendlyActor(ev.SourceId);
        var decision = AttackDisplayPolicy.Decide(
            file.AutoSettings,
            new AttackDisplayRequest(
                IsFriendly: isFriendly,
                HasAoe: false,
                IsAutoAttack: ev.IsAutoAttack,
                IsCast: false));
        if (decision == AttackDisplayDecision.None)
        {
            return;
        }

        var src = _objectTable.SearchById(ev.SourceId);
        var sourceWorld = src is null
            ? (Vector3?)null
            : new Vector3(src.Position.X, src.Position.Y, src.Position.Z);
        DrawAttackPulse(ev.ActionName, 1.8, sourceWorld, ev.IsAutoAttack ? "AA" : "Action");
    }

    private void DrawAttackPulse(string label, double durationSec, Vector3? sourceWorld, string prefix)
    {
        _minimap.AddArenaView(
            gimmick: "attack",
            callout: AttackPulseLabelPolicy.Format(prefix, label),
            durationSec: Math.Max(1.0, durationSec),
            direction: null,
            fanDeg: null,
            arenaRadius: 20.0,
            sourceWorld: sourceWorld);
    }

    private void PublishAutoSafeCall(CastStartedEvent ev, AutoSafeCall safeCall)
    {
        var actions = new List<ActionDefinition>
        {
            new()
            {
                Type = "tts",
                Text = safeCall.TtsText,
            },
        };

        _bus.Publish(new TriggerFiredEvent(
            Timestamp: DateTimeOffset.UtcNow,
            Zone: _currentZone,
            TriggerId: $"__auto_safe_call_{ev.CastActionId:X}_{ev.SourceId}",
            TriggerName: safeCall.IsEstimate ? $"推定安置：{ev.CastActionName}" : $"安置：{ev.CastActionName}",
            Actions: actions,
            SourceEvent: ev));
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
