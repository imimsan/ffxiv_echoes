using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using FfxivEchoes.Capture;
using FfxivEchoes.Events;
using FfxivEchoes.Triggers.Models;
using FfxivEchoes.Utils;
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
    private readonly IDisposable _combatStartSub;
    private readonly IDisposable _combatEndSub;

    private string _currentZone = "Unknown";

    // pre-pull の演出キャスト等でミニマップに AoE が出ないように、戦闘中だけ描画する。
    // 同パターン：MechanicTriggerService / AddObjectAoeService。
    private bool _inCombat;

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
        {
            _currentZone = string.IsNullOrEmpty(z.ZoneName) ? "Unknown" : z.ZoneName;
            // ゾーン跨ぎは強制 pre-combat 扱い（テレポ等で _inCombat が残らないように）。
            _inCombat = false;
        });
        _combatStartSub = bus.Subscribe<CombatStartedEvent>(_ => _inCombat = true);
        _combatEndSub = bus.Subscribe<CombatEndedEvent>(_ => _inCombat = false);
    }

    public void Dispose()
    {
        _castStartSub.Dispose();
        _actionSub.Dispose();
        _zoneSub.Dispose();
        _combatStartSub.Dispose();
        _combatEndSub.Dispose();
    }

    private void OnCastStart(CastStartedEvent ev)
    {
        // 戦闘外の演出キャスト（zone 入った直後のボス登場演出など）でミニマップに AoE を
        // 描かない。CastCapture は ICondition.InCombat と独立に publish するため、ここでゲート。
        if (!_inCombat)
        {
            return;
        }

        // self-target cast（target_id が caster 自身）は演出 / バフ / add 召喚系で
        // AoE 攻撃ではない。Lumina に EffectRange が登録されていても、本物の AoE
        // ではないため描画スキップ。
        // 例：月の底のゾディアーク本体「パラデイグマ」(0x67BF) は target_id=source_id で
        // ケツァク add を召喚する演出 cast だが、Lumina 上 Donut 形状を持つため誤って
        // ゾディアーク位置（アリーナ南端）を中心にドーナツが描画されていた。
        // 正規の PB AoE（自爆系の caster 中心 AoE）は target_id=null なのでこのフィルタに
        // 引っかからない。
        if (ev.TargetId is { } tid && tid == ev.SourceId)
        {
            _log.Information("[FfxivEchoes] AutoTelegraph: skip self-target cast {Name} (id=0x{Id:X4})",
                ev.CastActionName, ev.CastActionId);
            return;
        }

        var file = _store.GetByZone(_currentZone);
        if (!AutoAoeDisplayPolicy.IsEnabled(file))
        {
            // 「AoE が出ない」相談のときに最初に確認すべき設定。明示的にログを残す。
            _log.Information(
                "[FfxivEchoes] AutoTelegraph: SKIP — show_auto_telegraphs=OFF（ファイル設定タブで ON にしてください） cast={Name} id=0x{Id:X4}",
                ev.CastActionName, ev.CastActionId);
            return;
        }

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
            _log.Information("[FfxivEchoes] AutoTelegraph: skip duplicate cast id=0x{Id:X4} src={Src} within {Sec}s",
                ev.CastActionId, ev.SourceId, DedupWindowSeconds);
            return;
        }
        _lastDrawnAt[key] = now;

        // 全体攻撃マーク済はミニマップに範囲を描いても意味がない（回避不能）のでスキップ。
        if (AutoSafeCallPlanner.IsRaidWide(file, ev.CastActionId, ev.CastActionName))
        {
            _log.Information("[FfxivEchoes] AutoTelegraph: SKIP — 全体攻撃マーク済 {Name} (id=0x{Id:X4}) 解除するには「全体攻撃マーク済キャスト」一覧から",
                ev.CastActionName, ev.CastActionId);
            return;
        }

        var arena = AutoAoeDisplayPolicy.ResolveArena(file);

        // 全体攻撃ガード：半径がアリーナ半径とほぼ同じ以上の cast は「アリーナ全域 ≒
        // 回避不能」なのでミニマップに描く意味がない（caster を中心に描くと、source actor が
        // アリーナ中央に居るボス本体の場合、画面中央に巨大なドーナツが出てきて視界を覆う）。
        // 月の底のゾディアーク本体「パラデイグマ」(0x67BF) は Donut 形状で半径がアリーナ
        // 半径級のため、ここで skip しないと「パラデイグマ詠唱と同時に画面中央にドーナツ」
        // が出る（ユーザー報告の典型症状）。MinimapWindow.DrawActualAoeShape の 0.9 ガードと
        // 揃え、かつ CastType=2/5 限定だった条件を撤廃して Donut (6/7/10) も対象に含める。
        var preview = AoeResolver.Resolve(_dataManager, ev.CastActionId, _log);
        if (preview is not null &&
            preview.Radius >= arena.ArenaRadius * 0.9)
        {
            _log.Information(
                "[FfxivEchoes] AutoTelegraph: skip raid-wide-equivalent AoE {Name} radius={R}m castType={Ct}",
                ev.CastActionName, preview.Radius, preview.CastType);
            return;
        }

        var aoe = preview;
        var namedSafeCall = AutoSafeCallPlanner.CreateKnown(ev.CastActionId, ev.CastActionName);
        if (aoe is null && namedSafeCall is null)
        {
            // Lumina に EffectRange が無く、辞書 override も無いキャストは、適当な 10m 円を
            // 出すと実ギミックと矛盾する。手動 mechanic / AoE Zone に任せる。
            _log.Information(
                "[FfxivEchoes] AutoTelegraph: skip {Name} (id=0x{Id:X4}) — Lumina に AoE 情報無し / 辞書 override 無し。\n" +
                "  対処：集計タブでキャスト選択 → 攻略登録 mechanic を作成し、AoE Zone を手動追加",
                ev.CastActionName, ev.CastActionId);
            return;
        }

        var autoSettings = file!.AutoSettings;
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

        var src = _objectTable.FindByEntityOrObjectId(ev.SourceId);
        var sourceWorld = src is null
            ? (Vector3?)null
            : new Vector3(src.Position.X, src.Position.Y, src.Position.Z);

        if (aoe is null && namedSafeCall is not null)
        {
            var knownFacingAngleRad = ArenaProjection.UsesFacing(namedSafeCall.Gimmick) && src is not null
                ? ArenaProjection.RotationToMapAngleRad(src.Rotation)
                : (float?)null;
            var knownGeometry = KnownAoeGeometry.TryCreate(
                namedSafeCall,
                AutoSafeCallPlanner.RadiusOverride(ev.CastActionId, ev.CastActionName),
                arena,
                out var knownSpec);

            _minimap.AddArenaView(
                gimmick: namedSafeCall.Gimmick,
                callout: namedSafeCall.Callout,
                // キャスト時間 + 発動後 3 秒残す（「すぐ消えると困る」）
                durationSec: ev.CastTime + 3.0,
                direction: ArenaProjection.UsesFacing(namedSafeCall.Gimmick) ? "N" : null,
                fanDeg: namedSafeCall.FanDeg,
                arenaRadius: arena.ArenaRadius,
                directionAngleRad: knownFacingAngleRad,
                sourceWorld: sourceWorld,
                arenaShape: arena.ArenaShape,
                arenaWidth: arena.ArenaWidth,
                arenaDepth: arena.ArenaDepth,
                lockedArenaCenter: arena.LockedArenaCenter,
                aoeRadius: knownGeometry ? knownSpec.RadiusM : null,
                aoeHalfWidthM: knownGeometry && knownSpec.HalfWidthM > 0 ? knownSpec.HalfWidthM : null,
                aoeCastType: knownGeometry ? knownSpec.CastType : null,
                autoLuminaCastId: ev.CastActionId);
            PublishAutoSafeCall(ev, namedSafeCall);
            return;
        }

        var resolvedAoe = aoe!;
        var radius = AoeResolver.EffectiveRadius(resolvedAoe, src?.HitboxRadius ?? 0f);
        var shape = "circle";
        var inferredFromCaster = resolvedAoe.FromCaster;

        var worldPos = ResolveAoeWorldPosition(
            inferredFromCaster,
            sourceWorld,
            ev.TargetId,
            ev.TargetWorld,
            ResolveObjectWorld);
        if (worldPos is null)
        {
            _log.Warning("[FfxivEchoes] AutoTelegraph: 位置不明 cast={Name} src={SrcId} tgt={TgtId}",
                ev.CastActionName, ev.SourceId, ev.TargetId ?? 0);
            return;
        }

        _log.Information("[FfxivEchoes] AutoTelegraph: 描画 cast={Name} radius={R}m shape={S} pos=({X:0.0},{Z:0.0})",
            ev.CastActionName, radius, shape, worldPos.Value.X, worldPos.Value.Z);

        // 1. ミニマップ（俯瞰アリーナ図）に確定/推定ギミック表示
        var safeCall = namedSafeCall ?? AutoSafeCallPlanner.Create(resolvedAoe, ev.CastActionName);
        var visualCall = SelectVisualCall(resolvedAoe, safeCall, ev.CastActionName);
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
                // キャスト時間 + 発動後 3 秒残す（「すぐ消えると困る」）
                durationSec: ev.CastTime + 3.0,
                direction: ArenaProjection.UsesFacing(visualCall.Gimmick) ? "N" : null,
                fanDeg: visualCall.FanDeg,
                arenaRadius: arena.ArenaRadius,
                directionAngleRad: facingAngleRad,
                sourceWorld: worldPos,
                aoeRadius: radius,
                aoeCastType: resolvedAoe.CastType,
                aoeOmenId: resolvedAoe.OmenId,
                arenaShape: arena.ArenaShape,
                arenaWidth: arena.ArenaWidth,
                arenaDepth: arena.ArenaDepth,
                lockedArenaCenter: arena.LockedArenaCenter,
                autoLuminaCastId: ev.CastActionId);
        }

        // 2. ワールドオーバーレイの「床塗り」描画は ActorTrackedAoeService が
        //    毎フレーム actor 位置・向きを再評価する形で肩代わりする（Splatoon 流ライブ描画）。
        //    ここでは描画しない。AutoTelegraphService はミニマップ表示と TTS の責務のみに集中。
        //    ActorTrackedAoeService は CastStartedEvent を購読して登録、CastCanceledEvent で
        //    即時消去、cast time + 3s 後に自動失効する。

        if (safeCall is not null)
        {
            PublishAutoSafeCall(ev, safeCall);
        }
    }

    public static AutoSafeCall? SelectVisualCall(
        AoeResolver.AoeInfo? aoe,
        AutoSafeCall? safeCall,
        string actionName)
    {
        if (aoe is not null)
        {
            return AutoSafeCallPlanner.CreateVisual(aoe, actionName) ?? safeCall;
        }

        return safeCall;
    }

    private void OnActionUsed(ActionUsedEvent ev)
    {
        // 戦闘外のインスタント発動（NPC のアイドル動作など）でミニマップに AoE を描かない。
        if (!_inCombat)
        {
            return;
        }

        // 演出系アクション除外：target=null かつ target_world が placeholder 座標
        // (≈0, *, ≈0) の action は AoE 起点不明の演出系（月の底の「ケラノウス・エイドロン」
        // 0x67E1 等：ゾディアーク add が「ケツァクウァトル」名で発動）。
        // FromCaster=true で source 中心に描画すると ゾディアーク add の位置
        // (100, 0, 79) = アリーナ中央付近に「正体不明のドーナツ」が誤発火するため
        // 入口で skip する。caster 中心の正規 AoE は target_world にキャスター座標が
        // 入るため本フィルタに引っかからない。
        if ((ev.TargetId is null or 0) &&
            ev.TargetWorld is { } tw &&
            MathF.Abs(tw.X) < 0.1f && MathF.Abs(tw.Z) < 0.1f)
        {
            _log.Debug("[FfxivEchoes] AutoTelegraph(ActionUsed): skip 無効座標 action {Name} (id={Id:X4}) src={Src}",
                ev.ActionName, ev.ActionId, ev.SourceId);
            return;
        }

        var file = _store.GetByZone(_currentZone);
        var isFriendly = ev.IsPlayer || IsFriendlyActor(ev.SourceId);
        var isRaidWide = AutoSafeCallPlanner.IsRaidWide(file, ev.ActionId, ev.ActionName);
        if (ShouldSkipActionUsedTelegraph(file, ev, isFriendly, isRaidWide))
        {
            if (isRaidWide)
            {
                _log.Debug("[FfxivEchoes] AutoTelegraph(ActionUsed): skip raid-wide {Name} id={Id:X4}",
                    ev.ActionName, ev.ActionId);
            }
            return;
        }

        var autoSettings = file!.AutoSettings;
        var arena = AutoAoeDisplayPolicy.ResolveArena(file);

        // Lumina から AoE 情報を引く（cast 無しの瞬間アクションでも EffectRange は取得可能）
        var aoe = AoeResolver.Resolve(_dataManager, ev.ActionId, _log);

        var decision = AttackDisplayPolicy.Decide(
            autoSettings,
            new AttackDisplayRequest(
                IsFriendly: isFriendly,
                HasAoe: aoe is not null,
                IsAutoAttack: ev.IsAutoAttack,
                IsCast: false));
        if (decision == AttackDisplayDecision.None)
        {
            return;
        }

        var src = _objectTable.FindByEntityOrObjectId(ev.SourceId);
        var sourceWorld = src is null
            ? (Vector3?)null
            : new Vector3(src.Position.X, src.Position.Y, src.Position.Z);

        if (aoe is null)
        {
            return;
        }
        var radius = AoeResolver.EffectiveRadius(aoe, src?.HitboxRadius ?? 0f);
        var worldPos = ResolveAoeWorldPosition(
            aoe.FromCaster,
            sourceWorld,
            ev.TargetId,
            ev.TargetWorld,
            ResolveObjectWorld);

        // AoE 持ちの瞬間アクション：Lumina の正確な半径と形状でミニマップに描く。
        // キャスト時間が無い分、表示は短め（3 秒）。発動済みなので「次の予告」ではなく
        // 「今この範囲が爆発した」のフィードバック表示。
        var visualCall = AutoSafeCallPlanner.CreateVisual(aoe, ev.ActionName);
        if (visualCall is null)
        {
            return;
        }

        float? facingAngleRad = null;
        if (ArenaProjection.UsesFacing(visualCall.Gimmick) && src is not null)
        {
            facingAngleRad = ArenaProjection.RotationToMapAngleRad(src.Rotation);
        }

        _minimap.AddArenaView(
            gimmick: visualCall.Gimmick,
            callout: $"発動: {ev.ActionName}",
            durationSec: 3.0,
            direction: ArenaProjection.UsesFacing(visualCall.Gimmick) ? "N" : null,
            fanDeg: visualCall.FanDeg,
            arenaRadius: arena.ArenaRadius,
            directionAngleRad: facingAngleRad,
            sourceWorld: worldPos,
            aoeRadius: radius,
            aoeCastType: aoe.CastType,
            arenaShape: arena.ArenaShape,
            arenaWidth: arena.ArenaWidth,
            arenaDepth: arena.ArenaDepth,
            lockedArenaCenter: arena.LockedArenaCenter,
            aoeOmenId: aoe.OmenId,
            autoLuminaCastId: ev.ActionId);
    }

    public static bool ShouldSkipActionUsedTelegraph(
        TriggerFile? file,
        ActionUsedEvent ev,
        bool sourceIsFriendly,
        bool isRaidWide)
    {
        if (!AutoAoeDisplayPolicy.IsEnabled(file))
        {
            return true;
        }

        if (!AutoAoeDisplayPolicy.ShouldDrawInstantActionTelegraph(file))
        {
            return true;
        }

        return ev.IsPlayer || sourceIsFriendly || isRaidWide;
    }

    public static Vector3? ResolveAoeWorldPosition(
        bool fromCaster,
        Vector3? sourceWorld,
        uint? targetId,
        Vector3? targetWorld,
        Func<uint, Vector3?> targetLookup)
    {
        if (fromCaster)
        {
            return sourceWorld;
        }

        if (targetWorld is { } snapshot)
        {
            return snapshot;
        }

        return targetId is { } tid && tid != 0 ? targetLookup(tid) : sourceWorld;
    }

    private Vector3? ResolveObjectWorld(uint entityOrObjectId)
    {
        var obj = _objectTable.FindByEntityOrObjectId(entityOrObjectId);
        return obj is null ? null : new Vector3(obj.Position.X, obj.Position.Y, obj.Position.Z);
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
        var obj = _objectTable.FindByEntityOrObjectId(id);
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
