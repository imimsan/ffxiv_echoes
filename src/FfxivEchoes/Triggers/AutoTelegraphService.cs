using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using FfxivEchoes.Capture;
using FfxivEchoes.Events;
using FfxivEchoes.Triggers.Models;
using FfxivEchoes.Utils;
using FfxivEchoes.Windows;

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
    private readonly IActionLookup _actionLookup;
    private readonly IObjectTable _objectTable;
    private readonly WorldOverlayWindow _worldOverlay;
    private readonly IMinimapSink _minimap;
    private readonly TriggerStore _store;
    private readonly IEventBus _bus;
    private readonly IPluginLog _log;
    // generic 自動安置 TTS の抑制判定を MechanicTriggerService の発火条件と一致させるための分岐チェック。
    private readonly Func<string?, bool>? _branchActiveCheck;

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
        IActionLookup actionLookup,
        IObjectTable objectTable,
        WorldOverlayWindow worldOverlay,
        IMinimapSink minimap,
        TriggerStore store,
        IPluginLog log,
        Func<string?, bool>? branchActiveCheck = null)
    {
        _bus = bus;
        _actionLookup = actionLookup;
        _objectTable = objectTable;
        _worldOverlay = worldOverlay;
        _minimap = minimap;
        _store = store;
        _log = log;
        _branchActiveCheck = branchActiveCheck;

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
        var preview = AoeResolver.Resolve(_actionLookup, ev.CastActionId, _log);
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

        // Action lookup から AoE 情報を引く（cast 無しの瞬間アクションでも EffectRange は取得可能）
        var aoe = AoeResolver.Resolve(_actionLookup, ev.ActionId, _log);

        // placeholder target_world (≈0,*,≈0) の演出系判定。月の底「ケラノウス・エイドロン」
        // (0x67E1 等：ゾディアーク add が変身体で発動) は target=null かつ
        // target_world=(-0.015,-0.015,-0.015) として記録される。
        // 旧実装はここを早期 skip していたが、これだと FromCaster 型の正規 self-target AoE も
        // 巻き込んでしまう。aoe.FromCaster=true かつ source actor がアリーナ内なら
        // source 中心で描画継続する経路を追加（T1 §4 優先度3）。
        if (HasPlaceholderActionUsedTarget(ev))
        {
            if (aoe is not { FromCaster: true })
            {
                _log.Debug("[FfxivEchoes] AutoTelegraph(ActionUsed): skip 演出系 action {Name} (id={Id:X4}) src={Src} (FromCaster=false or no AoE)",
                    ev.ActionName, ev.ActionId, ev.SourceId);
                return;
            }
            // FromCaster: 下流の ResolveAoeWorldPosition で sourceWorld が選ばれる。
            // source 位置の妥当性は下流の ShouldDrawActionUsedAoeAtWorldPosition + arena 内チェックで担保。
        }

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
        if (!ShouldDrawActionUsedAoeAtWorldPosition(worldPos))
        {
            _log.Debug("[FfxivEchoes] AutoTelegraph(ActionUsed): skip 位置未解決 action {Name} (id={Id:X4}) src={Src}",
                ev.ActionName, ev.ActionId, ev.SourceId);
            return;
        }

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

    public static bool ShouldDrawActionUsedAoeAtWorldPosition(Vector3? worldPosition)
        => worldPosition is not null;

    /// <summary>
    /// target=null かつ target_world ≈ (0,*,0) の演出系 placeholder を判定する。
    /// 月の底ケラノウス・エイドロン (0x67E1) などのゾディアーク add 変身体由来 action は
    /// この pattern で記録される。0.1m 閾値は target_x = -0.015 (= 0 に丸める前の符号付き
    /// placeholder) も拾うため。
    /// </summary>
    public static bool HasPlaceholderActionUsedTarget(ActionUsedEvent ev)
    {
        if (ev.TargetId is not (null or 0)) return false;
        if (ev.TargetWorld is not { } tw) return false;
        return MathF.Abs(tw.X) < 0.1f && MathF.Abs(tw.Z) < 0.1f;
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
        // ユーザーが同じキャストに攻略 mechanic を割り当てて「読み上げる」場合、generic な自動安置コール
        // （「外周安置」等）は二重読みになるため抑制する。視覚（ミニマップ AddArenaView）は別経路で
        // 既に描かれているのでここでの return では消えない。
        // 重要：抑制条件は MechanicTriggerService が実際に読み上げる条件と厳密に一致させる。条件が緩いと
        // 「mechanic は撃たない（分岐棄却/トリガー不一致）のに自動安置だけ抑制」して両方無音化＝事故になる。
        var file = _store.GetByZone(_currentZone);
        if (file is not null && WillLegacyMechanicSpeak(file, ev))
        {
            _log.Debug(
                "[FfxivEchoes] AutoTelegraph: 自動安置TTSを抑制（mechanic が読み上げるため） cast={Name}",
                ev.CastActionName);
            return;
        }

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

    /// <summary>
    /// この cast に対して MechanicTriggerService の legacy(AttachedTo)経路が実際に mechanic を発火し、
    /// かつ TTS を読み上げるか。generic 自動安置コールの抑制可否判定に使う。
    /// </summary>
    /// <remarks>
    /// MechanicTriggerService.OnCastStart の legacy 発火条件（FindMechanicForCast に branchActiveCheck を
    /// 渡す + HasNoExplicitTriggers + IsBranchAllowed）と FireMechanic の「BuildReminderActions 非空」を
    /// 厳密に複製する。条件を緩めると、分岐棄却 mechanic や明示 Triggers を持つ mechanic（legacy では
    /// 撃たれない）まで「読み上げる」と誤判定し、自動安置も mechanic も両方無音化する事故になるため。
    /// 明示 Triggers を持つ mechanic（新パスで撃たれうる）は安全側で抑制対象から外す＝抑制しすぎない。
    /// </remarks>
    private bool WillLegacyMechanicSpeak(TriggerFile file, CastStartedEvent ev)
    {
        var (profile, mech) = StrategyPlanResolver.FindMechanicForCast(
            file, ev.CastActionId, ev.CastActionName, _branchActiveCheck);
        if (profile is null || mech is null)
        {
            return false;
        }
        var branchAllowed = _branchActiveCheck?.Invoke(mech.BranchId) ?? true;
        // 重い BuildReminderActions は cheap な発火可能性ゲートを通ったときだけ呼ぶ。
        if (!ShouldConsiderSuppress(mech.Triggers.Count, branchAllowed))
        {
            return false;
        }
        foreach (var a in StrategyPlanResolver.BuildReminderActions(file, profile, mech))
        {
            if (string.Equals(a.Type, "tts", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 自動安置コールの抑制を「検討してよい」か（＝MechanicTriggerService の legacy 経路が発火しうるか）。
    /// 明示 Triggers を持つ mechanic は legacy では撃たれず、分岐棄却 mechanic も撃たれないため、
    /// どちらも抑制対象から外す（外さないと両方無音化する回帰になる）。純ロジックでテスト可能。
    /// </summary>
    public static bool ShouldConsiderSuppress(int mechTriggerCount, bool branchAllowed)
        => mechTriggerCount == 0 && branchAllowed;

    private bool IsFriendlyActor(uint id)
    {
        var obj = _objectTable.FindByEntityOrObjectId(id);
        if (obj is null) return false;
        // ObjectKind: Pc=プレイヤー、BattleNpc=敵/NPC など。
        // PC（プレイヤーキャラ）なら友軍とみなしてスキップ。
        return obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Pc;
    }

    /// <summary>
    /// Action 情報から AoE 形状を推測。circle / donut / square のどれかを返す。
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
            var geom = _actionLookup.TryGet(actionId);
            if (geom is null)
            {
                return false;
            }
            var effectRange = geom.EffectRangeM;
            if (effectRange <= 0) return false;
            // EffectRange が異常に大きい一部の特殊アクション
            // （アリーナ全域 80m など）は描画しない
            if (effectRange > 50f)
            {
                _log.Debug("[FfxivEchoes] AutoTelegraph: skip oversized AoE id={Id:X4} range={R}m",
                    actionId, effectRange);
                return false;
            }

            // CastType: 1=ST, 2=Circle (target-centered), 3=Cone, 4=Line,
            //           5=PBAoE on caster, 6=Donut, 7+=L/Cross 等の特殊
            var castType = geom.CastType;
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
            _log.Warning(ex, "[FfxivEchoes] AutoTelegraph: Action 解決失敗 (id={Id})", actionId);
            return false;
        }
    }
}
