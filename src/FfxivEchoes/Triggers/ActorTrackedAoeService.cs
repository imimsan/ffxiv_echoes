using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FfxivEchoes.Capture;
using FfxivEchoes.Events;
using FfxivEchoes.Triggers.Models;
using FfxivEchoes.Utils;
using FfxivEchoes.Windows;

namespace FfxivEchoes.Triggers;

/// <summary>
/// Splatoon 流の actor 追跡型 AoE 可視化。CastStartedEvent ごとに
/// <see cref="AoeRegistration"/> を <c>_active</c> リストに積み、毎フレーム ImGui の
/// foreground draw list に直接描画する。
/// </summary>
/// <remarks>
/// <para>
/// 重要：このサービスは「ステートレスな per-frame 再評価」を採用している。
/// 毎フレーム頭で <c>_active</c> を期限切れフィルタしてから残ったすべてを再描画する。
/// 個々の <see cref="AoeRegistration"/> はキャスト終了 / cancel / expiry によってのみ消える。
/// </para>
/// <para>
/// rotation の解決は <see cref="CastRotationSnapshot"/> を介して
/// 「キャスト開始時点の actor.Rotation」を保持できる。FFXIV のボスキャストは
/// 開始で向きが lock される設計なので、cone / rect AoE は CastSnapshot を既定とする。
/// 円・ドーナツは向き不要なので Live。
/// </para>
/// <para>
/// 既存の <see cref="AutoTelegraphService"/> はミニマップ表示と「初回描画」だけを担う。
/// 床塗りライブ描画はこのサービスが完全に肩代わりする（毎フレーム actor を追跡するので
/// 「ボスがちょっとずつ動く」ケースに対応できる）。
/// </para>
/// </remarks>
public sealed class ActorTrackedAoeService : IDisposable
{
    private readonly IObjectTable _objectTable;
    private readonly IDataManager _dataManager;
    private readonly WorldOverlayWindow _worldOverlay;
    private readonly CastRotationSnapshot _snapshot;
    private readonly TriggerStore _store;
    private readonly IPluginLog _log;
    private readonly AoeSequenceScheduler _scheduler;

    private readonly List<AoeRegistration> _active = new();
    private readonly object _gate = new();
    private string _currentZone = "Unknown";

    private readonly IDisposable _castStartSub;
    private readonly IDisposable _castCancelSub;
    private readonly IDisposable _castCompleteSub;
    private readonly IDisposable _actionUsedSub;
    private readonly IDisposable _triggerFiredSub;
    private readonly IDisposable _combatStartSub;
    private readonly IDisposable _combatEndSub;
    private readonly IDisposable _zoneSub;
    private readonly IDisposable _drawerReg;

    // pre-pull の演出キャスト等で床塗りが出ないように、戦闘中だけ受け付ける。
    // 同パターン：MechanicTriggerService / AddObjectAoeService / AutoTelegraphService。
    private bool _inCombat;

    public ActorTrackedAoeService(
        IEventBus bus,
        IFramework framework,
        IDataManager dataManager,
        IObjectTable objectTable,
        WorldOverlayWindow worldOverlay,
        CastRotationSnapshot snapshot,
        TriggerStore store,
        IPluginLog log)
    {
        _dataManager = dataManager;
        _objectTable = objectTable;
        _worldOverlay = worldOverlay;
        _snapshot = snapshot;
        _store = store;
        _log = log;

        // 時間差シーケンス用スケジューラを内包。Framework.Update でステップ消化。
        _scheduler = new AoeSequenceScheduler(framework, OnSequenceStepFire, log);

        _castStartSub = bus.Subscribe<CastStartedEvent>(OnCastStarted);
        _castCancelSub = bus.Subscribe<CastCanceledEvent>(OnCastCanceled);
        // CastCompletedEvent → Impact フェーズ遷移（赤フラッシュ → Fade）
        _castCompleteSub = bus.Subscribe<CastCompletedEvent>(OnCastCompleted);
        // 即時アクション（cast 無し）も床塗り対象。CastStartedEvent と
        // ActionUsedEvent でミニマップ表示と非対称にならないようにする。
        _actionUsedSub = bus.Subscribe<ActionUsedEvent>(OnActionUsed);
        // 攻略タブで定義された mechanic（aoe_zones / aoe_sequence）を床塗りに流し込む。
        // ArenaViewHandler は同じ event をミニマップに描画するので二系統が並行する。
        _triggerFiredSub = bus.Subscribe<TriggerFiredEvent>(OnTriggerFired);
        _combatStartSub = bus.Subscribe<CombatStartedEvent>(_ => _inCombat = true);
        _combatEndSub = bus.Subscribe<CombatEndedEvent>(_ =>
        {
            _inCombat = false;
            ClearActiveState();
        });
        _zoneSub = bus.Subscribe<ZoneChangedEvent>(z =>
        {
            _currentZone = string.IsNullOrEmpty(z.ZoneName) ? "Unknown" : z.ZoneName;
            // ゾーン跨ぎは強制 pre-combat 扱い（テレポ等で _inCombat が残らないように）。
            _inCombat = false;
            ClearActiveState();
        });

        // ライブ描画コールバック登録：以後 WorldOverlayWindow.Draw() のたびに DrawAll が呼ばれる
        _drawerReg = _worldOverlay.RegisterLiveDrawer(DrawAll);
    }

    public void Dispose()
    {
        _castStartSub.Dispose();
        _castCancelSub.Dispose();
        _castCompleteSub.Dispose();
        _actionUsedSub.Dispose();
        _triggerFiredSub.Dispose();
        _combatStartSub.Dispose();
        _combatEndSub.Dispose();
        _zoneSub.Dispose();
        _drawerReg.Dispose();
        _scheduler.Dispose();
        ClearActiveState();
    }

    private void ClearActiveState()
    {
        lock (_gate)
        {
            _active.Clear();
            _pendingSequenceArenaCenter.Clear();
        }
        _scheduler.Clear();
    }

    /// <summary>
    /// 現在追跡中の AoE 件数。テスト・診断用途。
    /// </summary>
    public int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                return _active.Count;
            }
        }
    }

    private void OnCastStarted(CastStartedEvent ev)
    {
        // ── 共通ゲート ──────────────────────────────────
        // 戦闘外の演出キャスト（zone 入った直後のボス登場演出など）で床塗りを出さない。
        if (!_inCombat) return;
        var file = _store.GetByZone(_currentZone);
        if (!AutoAoeDisplayPolicy.IsEnabled(file)) return;
        if (IsFriendlyActor(ev.SourceId)) return;
        if (AutoSafeCallPlanner.IsRaidWide(file, ev.CastActionId, ev.CastActionName)) return;

        // 余韻時間：cast time + 3s。AoE が降ってから少し残して可読性確保。
        var expiresAt = ev.Timestamp.AddSeconds(ev.CastTime + 3.0);

        var reg = TryBuildRegistration(file, ev.SourceId, ev.CastActionId, ev.CastActionName,
            ev.TargetId, ev.TargetWorld, expiresAt);
        if (reg is null) return;

        // 全体攻撃ガード：半径がアリーナ半径とほぼ同じ以上の cast は床塗りで描く意味がない
        // （回避不能 = 全体扱い）。月の底のゾディアーク本体「パラデイグマ」(0x67BF) は
        // Donut 形状で source actor (ゾディアーク本体 = アリーナ中央付近) を中心に描画
        // されると「画面中央のドーナツ」になる。これを入口で skip する。
        // MinimapWindow.DrawActualAoeShape (0.9 倍ガード) および AutoTelegraphService.OnCastStart
        // と揃える。
        var arenaForGate = AutoAoeDisplayPolicy.ResolveArena(file);
        if (arenaForGate.ArenaRadius > 0 && reg.Radius >= arenaForGate.ArenaRadius * 0.9f)
        {
            _log.Debug("[FfxivEchoes] ActorTrackedAoe: skip raid-wide-equivalent cast {Name} (id=0x{Id:X4}) r={R}m arena={A}m",
                ev.CastActionName, ev.CastActionId, reg.Radius, arenaForGate.ArenaRadius);
            return;
        }

        // Phase: cast 開始 = 確定。常に Confirmed として登録。
        reg.Phase = AoePhase.Confirmed;
        reg.PhaseStartedAt = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            // Predicted フェーズで先行表示していたエントリがあれば置き換える（重複描画防止）。
            // 同 (sourceId, castId) ペアの Predicted エントリを除去してから新 Confirmed を Add。
            _active.RemoveAll(a =>
                a.Phase == AoePhase.Predicted &&
                a.SourceId == ev.SourceId &&
                a.CastId == ev.CastActionId);
            _active.Add(reg);
        }
        _log.Debug("[FfxivEchoes] ActorTrackedAoe: + cast {Name} (id=0x{Id:X4}) src={Src} shape={Sh} r={R}m exp={Exp}",
            ev.CastActionName, ev.CastActionId, ev.SourceId, reg.Shape, reg.Radius, expiresAt);
    }

    /// <summary>
    /// CastCompletedEvent でフェーズを Confirmed → Impact に遷移。
    /// 赤フラッシュ <see cref="ImpactFlashSec"/> 秒 → 自動的に Fade（DrawAll 内で遷移）。
    /// </summary>
    private void OnCastCompleted(CastCompletedEvent ev)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            foreach (var r in _active)
            {
                if (r.SourceId == ev.SourceId && r.CastId == ev.CastActionId &&
                    r.Phase == AoePhase.Confirmed)
                {
                    r.Phase = AoePhase.Impact;
                    r.PhaseStartedAt = now;
                    // ExpiresAt は cast time + 3s が残っていれば長め、フラッシュ + Fade 完了が
                    // 短ければ後者で上書きする（短すぎる cast の余韻を確保）。
                    var minExpiry = now.AddSeconds(ImpactFlashSec + FadeDurationSec);
                    if (r.ExpiresAt < minExpiry)
                    {
                        r.ExpiresAt = minExpiry;
                    }
                }
            }
        }
    }

    private void OnActionUsed(ActionUsedEvent ev)
    {
        // 即時アクション（cast 無し）。AA は除外、それ以外で AoE 持ちなら床塗り。
        if (ev.IsAutoAttack) return;
        // 戦闘外のインスタント発動（NPC のアイドル動作など）で床塗りを出さない。
        if (!_inCombat) return;

        // 演出系アクション除外：target_world が placeholder 座標 (~0, *, ~0) の action は
        // 「AoE 起点を持たない演出系」として描画スキップ。
        // 月の底のゾディアーク add (data_id=9020) が「ケツァクウァトル」名で発動する
        // 「ケラノウス・エイドロン」(0x67E1) はこのパターンで、target=null かつ
        // target_world=(-0.015, -0.015, -0.015) で記録される。これを source actor 位置に
        // 描画するとマップ中央付近に「正体不明のドーナツ」が誤発火する。
        if (ev.TargetWorld is { } tw &&
            MathF.Abs(tw.X) < 0.1f && MathF.Abs(tw.Z) < 0.1f)
        {
            _log.Debug("[FfxivEchoes] ActorTrackedAoe: skip 無効座標 action {Name} (id=0x{Id:X4}) src={Src}",
                ev.ActionName, ev.ActionId, ev.SourceId);
            return;
        }

        var file = _store.GetByZone(_currentZone);
        if (!AutoAoeDisplayPolicy.IsEnabled(file)) return;
        if (!AutoAoeDisplayPolicy.ShouldDrawInstantActionTelegraph(file)) return;
        if (ev.IsPlayer) return;
        if (IsFriendlyActor(ev.SourceId)) return;
        if (AutoSafeCallPlanner.IsRaidWide(file, ev.ActionId, ev.ActionName)) return;

        // dedup：同 (SourceId, ActionId) のエントリが既に _active にあれば処理を変える。
        //   - AutoLumina：既に CastStarted で登録済 → skip
        //   - Predicted（録画予測由来）：既に薄く描かれている → Impact に昇格して着弾フラッシュ
        //   - UserDefined：mechanic 経由で登録済 → skip
        // これで「Predicted で先行表示 → action_used で即 Impact フラッシュ」が連動する。
        // インスタント技でも Predicted 経路が動けば「事前 → 着弾」のフローが両立する。
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            for (var i = 0; i < _active.Count; i++)
            {
                var a = _active[i];
                if (a.SourceId != ev.SourceId || a.CastId != ev.ActionId) continue;

                if (a.Source == AoeRegistrationSource.Predicted)
                {
                    // 録画予測由来の Predicted 描画が既にあれば、Impact に昇格して着弾を強調
                    a.Phase = AoePhase.Impact;
                    a.PhaseStartedAt = now;
                    a.ExpiresAt = now.AddSeconds(ImpactFlashSec + FadeDurationSec);
                    _log.Debug("[FfxivEchoes] ActorTrackedAoe: Predicted → Impact 昇格 {Name} (id=0x{Id:X4})",
                        ev.ActionName, ev.ActionId);
                    return;
                }

                _log.Debug("[FfxivEchoes] ActorTrackedAoe: skip dup instant {Name} (id=0x{Id:X4}) — 既存 phase={P} source={S}",
                    ev.ActionName, ev.ActionId, a.Phase, a.Source);
                return;
            }
        }

        // ノーキャストテレグラフ（絶コンテンツ多数）：cast 開始予告が無いので Impact フェーズで
        // 直接登録（FadeDurationSec のフェードアウトで「今爆発した」を視覚化）。
        // Predicted が間に合っていれば上の dedup で昇格処理されるが、未登録の場合は事後表示。
        var expiresAt = ev.Timestamp.AddSeconds(ImpactFlashSec + FadeDurationSec);

        // 即時パスでも AoE 情報が無い action は出さない（過剰演出になるため）
        var reg = TryBuildRegistration(file, ev.SourceId, ev.ActionId, ev.ActionName,
            ev.TargetId, ev.TargetWorld, expiresAt);
        if (reg is null) return;

        // 直接 Impact フェーズで登録：cast 経由しない＝予告なしの「今降ってきた」表示
        reg.Phase = AoePhase.Impact;
        reg.PhaseStartedAt = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            _active.Add(reg);
        }
        _log.Debug("[FfxivEchoes] ActorTrackedAoe: + instant {Name} (id=0x{Id:X4}) src={Src} shape={Sh} r={R}m phase=Impact",
            ev.ActionName, ev.ActionId, ev.SourceId, reg.Shape, reg.Radius);
    }

    private void OnCastCanceled(CastCanceledEvent ev)
    {
        // インタラプトされた AoE は降ってこないので即座に消す
        lock (_gate)
        {
            var removed = _active.RemoveAll(a => a.SourceId == ev.SourceId && a.CastId == ev.CastActionId);
            if (removed > 0)
            {
                _log.Debug("[FfxivEchoes] ActorTrackedAoe: - {Name} (id=0x{Id:X4}) cancel src={Src}",
                    ev.CastActionName, ev.CastActionId, ev.SourceId);
            }
        }
        // 連鎖シーケンスの未発火ステップも一緒に取り消す
        _scheduler.Cancel(BuildSequenceKey(ev.SourceId, ev.CastActionId));
    }

    /// <summary>
    /// MechanicTriggerService 等から発行された <see cref="TriggerFiredEvent"/> を受け、
    /// 各 ActionDefinition.AoeZones を per-frame 床塗り経路に流し込む。
    /// 同じ event をミニマップに描画する <see cref="Actions.Handlers.ArenaViewHandler"/> と
    /// 並行する（俯瞰 + 床塗りの両方を同時提供）。
    /// </summary>
    private void OnTriggerFired(TriggerFiredEvent ev)
    {
        var sourceCastId = ExtractCastId(ev.SourceEvent);
        var (sourceCastName, _) = ExtractCastName(ev.SourceEvent);
        var arenaCenter = ResolveArenaCenter(ev);
        var triggerKey = ev.TriggerId ?? string.Empty;

        var index = 0;
        foreach (var action in ev.Actions)
        {
            // arena_view 以外でも将来 AoeZones を持つアクションが出るかもしれないので
            // 型では絞らず action.AoeZones の存在で判定する。
            if (action.AoeZones is null || action.AoeZones.Count == 0) continue;

            var actionDuration = action.Duration ?? 5.0;
            foreach (var zone in action.AoeZones)
            {
                if (!zone.LiveFloorPaint) continue;

                var dur = zone.DurationSec ?? actionDuration;
                var expiresAt = ev.Timestamp.AddSeconds(dur);
                var zoneIdSuffix = string.IsNullOrEmpty(zone.Id) ? $"#{index}" : zone.Id;
                var fullZoneId = $"{triggerKey}::{zoneIdSuffix}";

                Track(zone, ev.SourceEvent, arenaCenter, expiresAt, fullZoneId);
                index++;
            }
        }

        // 連鎖シーケンス：StrategyPlanResolver が ActionDefinition.AoeSequence にコピーしてあるので
        // ここで scheduler に流す。
        foreach (var action in ev.Actions)
        {
            if (action.AoeSequence is { } seq && seq.Steps.Count > 0)
            {
                TrackSequence(seq, ev.SourceEvent, arenaCenter, ev.Timestamp);
            }
        }
    }

    /// <summary>
    /// 攻略タブで定義された <see cref="StrategyAoeZone"/> を per-frame 床塗りに登録する公開 API。
    /// MechanicTriggerService 経由（TriggerFiredEvent）からも、外部直接呼び出しでも使える。
    /// </summary>
    /// <param name="zone">描画する zone 定義</param>
    /// <param name="sourceEvent">anchor 解決と castId 取得に使う発火元イベント</param>
    /// <param name="arenaCenter">anchor=static のときの基準点（プロファイルから引いたアリーナ中心）</param>
    /// <param name="expiresAt">自動消滅時刻</param>
    /// <param name="zoneId">dedup キー（同 zoneId は重複登録しない）</param>
    public void Track(
        StrategyAoeZone zone,
        IGameEvent? sourceEvent,
        Vector3 arenaCenter,
        DateTimeOffset expiresAt,
        string zoneId)
    {
        var shape = ParseZoneShape(zone.Shape);
        if (shape is null)
        {
            _log.Warning("[FfxivEchoes] ActorTrackedAoe: 不明な shape '{Shape}' (zoneId={Id})", zone.Shape, zoneId);
            return;
        }

        var anchors = AoeAnchorResolver.Resolve(zone, arenaCenter, sourceEvent, _objectTable);
        if (anchors.Count == 0)
        {
            _log.Debug("[FfxivEchoes] ActorTrackedAoe: anchor 未解決 zone={Id} anchor={Anchor}",
                zoneId, zone.Anchor ?? "static");
            return;
        }

        var castId = ExtractCastId(sourceEvent);
        var (castName, _) = ExtractCastName(sourceEvent);

        var (rotKind, overrideRad) = ResolveRotationKindFromZone(zone, shape.Value);
        var color = string.IsNullOrEmpty(zone.Color)
            ? (zone.IsDanger ? "#FF6464" : "#64FF64")
            : zone.Color;

        var added = 0;
        for (var i = 0; i < anchors.Count; i++)
        {
            var anchor = anchors[i];
            // each_matched_object などで複数結果が返るときは zoneId に index を付ける
            var perAnchorZoneId = anchors.Count > 1 ? $"{zoneId}#{i}" : zoneId;

            // 静的位置：固定座標 + zone offset を世界座標に焼く（actor 解決を skip）
            Vector3? staticPos = null;
            uint sourceIdForReg = 0;
            float offX = 0f, offZ = 0f;
            var actorHitbox = 0f;
            if (anchor.StaticWorldPos is { } sp)
            {
                staticPos = new Vector3(
                    sp.X + (float)zone.X,
                    sp.Y,
                    sp.Z + (float)zone.Z);
            }
            else if (anchor.ActorId is { } aid)
            {
                sourceIdForReg = aid;
                offX = (float)zone.X;
                offZ = (float)zone.Z;
                actorHitbox = _objectTable.FindByEntityOrObjectId(aid)?.HitboxRadius ?? 0f;
            }

            var anchorRotKind = rotKind;
            var anchorOverrideRad = overrideRad;
            if (staticPos is { } staticWorld &&
                ShouldPointStaticObjectAoeToCenter(zone, sourceEvent, shape.Value))
            {
                anchorRotKind = RotationKind.Override;
                anchorOverrideRad = AoeGeometryPolicy.FfxivRotationTowardsArenaCenter(staticWorld, arenaCenter);
            }

            var radius = ApplyHitboxRadius((float)zone.RadiusM, actorHitbox, zone.IncludeHitbox);
            var reg = new AoeRegistration
            {
                SourceId = sourceIdForReg,
                CastId = castId,
                CastName = string.IsNullOrEmpty(zone.Label) ? castName : zone.Label!,
                Shape = shape.Value,
                Radius = radius,
                InnerRadius = (float)(zone.InnerRadiusM ?? radius * 0.3f),
                FanDeg = (float)(zone.FanDeg ?? 90.0),
                HalfWidth = ResolveHalfWidth(shape.Value, zone.HalfWidthM),
                FromCaster = true,
                TargetId = null,
                RotationKind = anchorRotKind,
                OverrideRotationRad = anchorOverrideRad,
                RotationOffsetRad = DegreesToRadians((float)(zone.RotationOffsetDeg ?? 0.0)),
                ColorHex = color,
                ExpiresAt = expiresAt,
                Source = AoeRegistrationSource.UserDefined,
                ZoneId = perAnchorZoneId,
                Matcher = zone.ActorMatcher,
                Filter = zone.StateFilter,
                StaticWorldPos = staticPos,
                ZoneOffsetX = offX,
                ZoneOffsetZ = offZ,
                SuppressAutoLumina = zone.SuppressAutoAoe,
            };

            lock (_gate)
            {
                // 同 ZoneId が既存なら skip（dedup）
                var exists = false;
                for (var k = 0; k < _active.Count; k++)
                {
                    var a = _active[k];
                    if (a.Source == AoeRegistrationSource.UserDefined && a.ZoneId == perAnchorZoneId)
                    {
                        exists = true;
                        break;
                    }
                }
                if (exists) continue;
                _active.Add(reg);
                added++;

                // suppress: 同 cast id の AutoLumina エントリを除去
                if (zone.SuppressAutoAoe && castId != 0)
                {
                    _active.RemoveAll(a => a.Source == AoeRegistrationSource.AutoLumina && a.CastId == castId);
                }
            }
        }

        if (added > 0)
        {
            _log.Debug("[FfxivEchoes] ActorTrackedAoe: + user zone={Id} shape={Sh} anchors={N}",
                zoneId, shape, anchors.Count);
        }
    }

    /// <summary>
    /// 録画予測由来の「予告フェーズ」AoE を登録する。<see cref="PredictedCastReminderService"/>
    /// から呼ばれる。同 (sourceId, castId) の Predicted エントリが既にあれば skip（冪等）。
    /// CastStartedEvent が来たら <see cref="OnCastStarted"/> 内で Confirmed に置き換え。
    /// 来なければ predictedFireAt + 1 秒 で自動消滅。
    /// </summary>
    /// <param name="sourceId">予測対象のボス id（0 は無効として skip）</param>
    /// <param name="castId">予測対象の cast id（Lumina で AoE 解決可能なもの）</param>
    /// <param name="castName">表示用の名前</param>
    /// <param name="predictedFireAt">予測着弾時刻（録画ベースの絶対時刻）</param>
    public void TrackPredicted(uint sourceId, uint castId, string castName, DateTimeOffset predictedFireAt)
    {
        // sourceId=0 は SearchById が意図しないオブジェクトを返す可能性があるため skip。
        // ミニマップ表示は別経路 (PredictedCastReminderService.TryDrawPredictedMarker) が
        // 担当するので、床塗りが出ないだけで機能損失は許容範囲。
        if (sourceId == 0) return;

        var file = _store.GetByZone(_currentZone);
        if (!AutoAoeDisplayPolicy.IsEnabled(file)) return;
        if (AutoSafeCallPlanner.IsRaidWide(file, castId, castName)) return;

        var aoe = AoeResolver.Resolve(_dataManager, castId, _log);
        if (aoe is null)
        {
            // Lumina に EffectRange 無し（flavor cast）は予告できない。確定時の fallback に任せる。
            return;
        }

        // dedup：同じ Predicted エントリが既に登録済なら冪等 skip
        lock (_gate)
        {
            for (var i = 0; i < _active.Count; i++)
            {
                var a = _active[i];
                if (a.Phase == AoePhase.Predicted &&
                    a.SourceId == sourceId &&
                    a.CastId == castId)
                {
                    return;
                }
            }
        }

        // ExpiresAt：予測着弾 + 1 秒で自動消滅。CastStarted が来れば Confirmed 置き換えで
        // ExpiresAt が cast time + 3s に再設定される。
        var expiresAt = predictedFireAt.AddSeconds(1.0);

        if (TryInferShape(aoe.CastType, aoe.OmenId) is not { } shape)
        {
            return;
        }
        var rotKind = UsesCastSnapshot(shape)
            ? RotationKind.CastSnapshot
            : RotationKind.Live;
        var colorHex = shape == TrackedAoeShape.Donut ? "#FFB84D" : "#FF6464";

        var fanDeg = 90f;
        if (shape == TrackedAoeShape.Cone)
        {
            var named = AutoSafeCallPlanner.CreateKnown(castId, castName);
            if (named?.FanDeg is { } fd)
            {
                fanDeg = (float)fd;
            }
        }

        var radius = EffectiveRadius(aoe, sourceId);
        var reg = new AoeRegistration
        {
            SourceId = sourceId,
            CastId = castId,
            CastName = castName,
            Shape = shape,
            Radius = radius,
            InnerRadius = shape == TrackedAoeShape.Donut ? radius * AoeResolver.DonutInnerRatio(aoe.OmenId) : 0f,
            FanDeg = shape == TrackedAoeShape.Cone ? fanDeg : 0f,
            HalfWidth = DefaultHalfWidth(shape),
            FromCaster = aoe.FromCaster,
            TargetId = null,
            RotationKind = rotKind,
            ColorHex = colorHex,
            ExpiresAt = expiresAt,
            Source = AoeRegistrationSource.Predicted,
            Phase = AoePhase.Predicted,
            PhaseStartedAt = DateTimeOffset.UtcNow,
            PredictedFireAt = predictedFireAt,
        };

        lock (_gate)
        {
            _active.Add(reg);
        }
        _log.Debug("[FfxivEchoes] ActorTrackedAoe: + predicted {Name} (id=0x{Id:X4}) src={Src} shape={Sh} fireAt={Fire}",
            castName, castId, sourceId, shape, predictedFireAt);
    }

    /// <summary>
    /// <see cref="AoeSequence"/> をスケジューラに渡す。MechanicTriggerService から直接呼ぶ。
    /// 各ステップは <see cref="OnSequenceStepFire"/> 経由で <see cref="Track"/> を呼ぶ。
    /// </summary>
    public void TrackSequence(
        AoeSequence sequence,
        IGameEvent sourceEvent,
        Vector3 arenaCenter,
        DateTimeOffset fireBaseAt)
    {
        if (sequence?.Steps is null || sequence.Steps.Count == 0) return;
        var castId = ExtractCastId(sourceEvent);
        var srcId = AoeAnchorResolver.ResolveSourceActorId(sourceEvent);
        var key = BuildSequenceKey(srcId, castId);
        // arena center をスケジュール実行時のクロージャに焼く
        _pendingSequenceArenaCenter[key] = arenaCenter;
        _scheduler.Schedule(sequence, sourceEvent, srcId, key, fireBaseAt);
    }

    private readonly Dictionary<string, Vector3> _pendingSequenceArenaCenter = new();

    private void OnSequenceStepFire(AoeSequenceStep step, IGameEvent source, uint sourceActorId, string mechanicKey)
    {
        var arenaCenter = _pendingSequenceArenaCenter.TryGetValue(mechanicKey, out var ac)
            ? ac : Vector3.Zero;
        var stepDuration = step.DurationSec ?? 3.0;
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(stepDuration);
        var stepLabel = step.Label ?? string.Empty;

        var idx = 0;
        foreach (var zone in step.Zones)
        {
            var zid = $"seq::{mechanicKey}::{stepLabel}::{idx}";
            Track(zone, source, arenaCenter, expiresAt, zid);
            idx++;
        }
    }

    private static string BuildSequenceKey(uint sourceId, uint castId) => $"{sourceId}/0x{castId:X}";

    /// <summary>
    /// <see cref="StrategyAoeZone.Shape"/> 文字列を内部 enum に変換。
    /// 不明値は null（呼び出し側で warn）。
    /// </summary>
    public static TrackedAoeShape? ParseZoneShape(string? shape) =>
        (shape ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "circle" => TrackedAoeShape.Circle,
            "donut" => TrackedAoeShape.Donut,
            "cone" => TrackedAoeShape.Cone,
            "rect" or "line" => TrackedAoeShape.Rect,
            "cross" => TrackedAoeShape.Cross,
            "donut_cone" or "donutcone" => TrackedAoeShape.DonutCone,
            "half_plane" or "halfplane" => TrackedAoeShape.HalfPlane,
            _ => null,
        };

    /// <summary>
    /// <see cref="StrategyAoeZone.RotationSource"/> + <see cref="StrategyAoeZone.RotationDeg"/>
    /// から <see cref="RotationKind"/> + 上書き角度を解決。
    /// </summary>
    private static (RotationKind kind, float? overrideRad) ResolveRotationKindFromZone(
        StrategyAoeZone zone, TrackedAoeShape shape)
    {
        // 明示の rotation_source が最優先
        if (zone.RotationSource is { } rs)
        {
            if (rs.Kind == RotationKind.Override && rs.OverrideDeg is { } deg)
            {
                // OverrideDeg は render 系（0=東）。FFXIV rad に変換して保持。
                var renderRad = (float)(deg * Math.PI / 180.0);
                var ffxivRad = MathF.PI / 2f - renderRad;
                return (RotationKind.Override, ffxivRad);
            }
            return (rs.Kind, null);
        }

        // RotationSource 未指定で zone.RotationDeg があれば Override 扱い
        if (zone.RotationDeg is { } zr && NeedsRotation(shape))
        {
            var renderRad = (float)(zr * Math.PI / 180.0);
            var ffxivRad = MathF.PI / 2f - renderRad;
            return (RotationKind.Override, ffxivRad);
        }

        // 既定：rotation 必要 shape は CastSnapshot、不要なら Live
        return NeedsRotation(shape)
            ? (RotationKind.CastSnapshot, null)
            : (RotationKind.Live, null);
    }

    private static bool NeedsRotation(TrackedAoeShape shape) => shape is
        TrackedAoeShape.Cone or TrackedAoeShape.Rect or TrackedAoeShape.Cross
        or TrackedAoeShape.DonutCone or TrackedAoeShape.HalfPlane;

    private static bool ShouldPointStaticObjectAoeToCenter(
        StrategyAoeZone zone,
        IGameEvent? sourceEvent,
        TrackedAoeShape shape)
    {
        if (!NeedsRotation(shape))
        {
            return false;
        }

        if (sourceEvent is not ObjectAppearedEvent and not ObjectGroupAppearedEvent)
        {
            return false;
        }

        if (zone.RotationDeg is not null)
        {
            return false;
        }

        // 既存の自動生成データには CastSnapshot が入っていることがあるが、
        // ObjectAppearedEvent には詠唱向きが無いので中心向きに補正する。
        if (zone.RotationSource is { Kind: not RotationKind.CastSnapshot })
        {
            return false;
        }

        var anchor = (zone.Anchor ?? string.Empty).Trim().ToLowerInvariant();
        return anchor is "matched_object" or "each_matched_object";
    }

    private static uint ExtractCastId(IGameEvent? ev) => ev switch
    {
        CastStartedEvent c => c.CastActionId,
        CastCompletedEvent c => c.CastActionId,
        CastCanceledEvent c => c.CastActionId,
        ActionUsedEvent a => a.ActionId,
        _ => 0u,
    };

    private static (string name, uint id) ExtractCastName(IGameEvent? ev) => ev switch
    {
        CastStartedEvent c => (c.CastActionName, c.CastActionId),
        CastCompletedEvent c => (c.CastActionName, c.CastActionId),
        CastCanceledEvent c => (c.CastActionName, c.CastActionId),
        ActionUsedEvent a => (a.ActionName, a.ActionId),
        _ => (string.Empty, 0u),
    };

    /// <summary>
    /// TriggerFiredEvent の Actions から arena_view が持つ ArenaCenter を読み出す。
    /// 見つからなければ Vector3.Zero（プロファイル既定）を返す。
    /// </summary>
    private static Vector3 ResolveArenaCenter(TriggerFiredEvent ev)
    {
        foreach (var action in ev.Actions)
        {
            if (action.ArenaCenterX is { } cx && action.ArenaCenterZ is { } cz)
            {
                return new Vector3((float)cx, 0f, (float)cz);
            }
        }
        return Vector3.Zero;
    }

    /// <summary>
    /// (cast id, name, target) から AoE を Lumina で解決し <see cref="AoeRegistration"/> を組む。
    /// 解決できない場合は不正確な推測円を出さず、登録しない。
    /// </summary>
    private AoeRegistration? TryBuildRegistration(
        Models.TriggerFile? file,
        uint sourceId, uint actionId, string actionName, uint? targetId, Vector3? targetWorld,
        DateTimeOffset expiresAt)
    {
        var arena = AutoAoeDisplayPolicy.ResolveArena(file);
        var aoe = AoeResolver.Resolve(_dataManager, actionId, _log);
        var source = _objectTable.FindByEntityOrObjectId(sourceId);
        Vector3? sourceWorld = source is null
            ? null
            : new Vector3(source.Position.X, source.Position.Y, source.Position.Z);

        // 異常巨大 AoE はスキップ（アリーナ全体を覆う設定は意味が無い）
        if (aoe is not null && aoe.CastType is 2 or 5 && aoe.Radius >= arena.ArenaRadius * 1.5)
        {
            return null;
        }

        if (aoe is null)
        {
            var known = AutoSafeCallPlanner.CreateKnown(actionId, actionName);
            if (!KnownAoeGeometry.TryCreate(
                    known,
                    AutoSafeCallPlanner.RadiusOverride(actionId, actionName),
                    arena,
                    out var knownSpec))
            {
                return null;
            }

            var knownShape = ParseZoneShape(knownSpec.Shape);
            if (knownShape is null)
            {
                return null;
            }

            return new AoeRegistration
            {
                SourceId = sourceId,
                CastId = actionId,
                CastName = actionName,
                Shape = knownShape.Value,
                Radius = knownSpec.RadiusM,
                InnerRadius = knownSpec.InnerRadiusM,
                FanDeg = knownShape == TrackedAoeShape.Cone ? knownSpec.FanDeg : 0f,
                HalfWidth = knownSpec.HalfWidthM,
                FromCaster = true,
                TargetId = targetId,
                TargetWorld = targetWorld,
                RotationKind = NeedsRotation(knownShape.Value) ? RotationKind.CastSnapshot : RotationKind.Live,
                ColorHex = knownShape == TrackedAoeShape.Donut ? "#FFB84D" : "#FF6464",
                ExpiresAt = expiresAt,
                StaticWorldPos = sourceWorld,
            };
        }

        if (TryInferShape(aoe.CastType, aoe.OmenId) is not { } shape)
        {
            return null;
        }
        // rotation 必要な shape は CastSnapshot で固定（ボスキャストは開始で向き lock）
        var rotKind = UsesCastSnapshot(shape)
            ? RotationKind.CastSnapshot
            : RotationKind.Live;
        var colorHex = shape == TrackedAoeShape.Donut ? "#FFB84D" : "#FF6464";

        // 扇の角度：SafeCallDictionary に override があればそれを優先（学習辞書の精度）。
        // 無ければ FFXIV の標準的な 90° を既定。
        var fanDeg = 90f;
        if (shape == TrackedAoeShape.Cone)
        {
            var named = AutoSafeCallPlanner.CreateKnown(actionId, actionName);
            if (named?.FanDeg is { } fd)
            {
                fanDeg = (float)fd;
            }
        }

        var radius = AoeResolver.EffectiveRadius(aoe, source?.HitboxRadius ?? 0f);
        return new AoeRegistration
        {
            SourceId = sourceId,
            CastId = actionId,
            CastName = actionName,
            Shape = shape,
            Radius = radius,
            InnerRadius = shape == TrackedAoeShape.Donut ? radius * AoeResolver.DonutInnerRatio(aoe.OmenId) : 0f,
            FanDeg = shape == TrackedAoeShape.Cone ? fanDeg : 0f,
            // 直線 / 十字 AoE の半幅は Lumina に無いので 2.5m 既定（FFXIV 標準的な line 幅）
            HalfWidth = DefaultHalfWidth(shape),
            FromCaster = aoe.FromCaster,
            TargetId = targetId,
            TargetWorld = targetWorld,
            RotationKind = rotKind,
            ColorHex = colorHex,
            ExpiresAt = expiresAt,
            StaticWorldPos = SelectStaticSourceSnapshot(aoe.FromCaster, sourceWorld),
        };
    }

    /// <summary>
    /// Lumina の CastType を内部 shape 列挙に変換。テスト用に切り出した純粋関数。
    /// </summary>
    /// <remarks>
    /// CastType: 2 = target-centered circle / 3 = cone / 4 = line (rect) /
    /// 5 = PBAoE on caster / 6, 7, 10 = donut variants。
    /// 未知の CastType は推測で描かず、明示定義に任せる。
    /// </remarks>
    public static TrackedAoeShape InferShape(int castType, uint omenId = 0)
        => TryInferShape(castType, omenId)
           ?? throw new ArgumentOutOfRangeException(nameof(castType), castType, "Unsupported Lumina cast type.");

    public static TrackedAoeShape? TryInferShape(int castType, uint omenId = 0)
    {
        if (AoeResolver.IsDonutShape(castType, omenId))
        {
            return TrackedAoeShape.Donut;
        }

        return castType switch
        {
            2 or 5 => TrackedAoeShape.Circle,
            11 => TrackedAoeShape.Cross,
            3 => TrackedAoeShape.Cone,
            4 or 12 => TrackedAoeShape.Rect,
            13 => TrackedAoeShape.Cone,
            _ => null,
        };
    }

    private static bool UsesCastSnapshot(TrackedAoeShape shape)
        => shape is TrackedAoeShape.Cone or TrackedAoeShape.Rect or TrackedAoeShape.Cross;

    public static Vector3? SelectStaticSourceSnapshot(bool fromCaster, Vector3? sourceWorld)
        => fromCaster ? sourceWorld : null;

    public static float ApplyHitboxRadius(float radius, float hitboxRadius, bool includeHitbox)
        => includeHitbox ? radius + Math.Max(0f, hitboxRadius) : radius;

    public static float ApplyRenderRotationOffset(float renderRotation, float rotationOffsetRad)
        => renderRotation + rotationOffsetRad;

    private static float DegreesToRadians(float degrees)
        => degrees * MathF.PI / 180f;

    private static float DefaultHalfWidth(TrackedAoeShape shape)
        => shape is TrackedAoeShape.Rect or TrackedAoeShape.Cross or TrackedAoeShape.HalfPlane
            ? AoeGeometryPolicy.DefaultLineHalfWidthM
            : 0f;

    public static float ResolveHalfWidthForShape(TrackedAoeShape shape, double? requested)
        => shape is TrackedAoeShape.Rect or TrackedAoeShape.Cross or TrackedAoeShape.HalfPlane
            ? AoeGeometryPolicy.ResolveLineHalfWidth(requested)
            : (float)(requested ?? 0.0);

    private static float ResolveHalfWidth(TrackedAoeShape shape, double? requested)
        => ResolveHalfWidthForShape(shape, requested);

    private float EffectiveRadius(AoeResolver.AoeInfo aoe, uint sourceId)
    {
        var source = _objectTable.FindByEntityOrObjectId(sourceId);
        return AoeResolver.EffectiveRadius(aoe, source?.HitboxRadius ?? 0f);
    }

    /// <summary>
    /// FFXIV の rotation（0=南、+CW）→ ImGui 描画系角度（0=東、π/2=南）に変換。
    /// テスト用の純粋関数。<see cref="ArenaProjection.RotationToMapAngleRad"/> と同じ式。
    /// </summary>
    public static float ConvertFfxivRotationToRender(float ffxivRotation)
        => MathF.PI / 2f - ffxivRotation;

    /// <summary>
    /// actor (ax, az) から target (tx, tz) へ向く FFXIV rotation を計算。
    /// FFXIV の rotation 体系（0=南=+Z 方向）に合わせる：南（dz&gt;0, dx=0）で 0、
    /// 東（dx&gt;0, dz=0）で π/2、北で π or -π、西で -π/2。
    /// </summary>
    public static float ComputeTowardsTargetRotation(float dx, float dz)
        => MathF.Atan2(dx, dz);

    /// <summary>
    /// 毎フレーム呼ばれるドロワー。期限切れを掃いてから現存の登録を全部描く。
    /// </summary>
    private void DrawAll(ImDrawListPtr draw, IGameGui gameGui)
    {
        // 設定 OFF 時は描画自体を skip。登録は維持する（再 ON 時に即復帰させるため）。
        // ミニマップ表示は別経路 (ShowAutoTelegraphs) で独立に制御。
        var file = _store.GetByZone(_currentZone);
        if (file?.AutoSettings.ShowFloorPaint == false)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        AoeRegistration[] regs;
        lock (_gate)
        {
            _active.RemoveAll(a => a.ExpiresAt <= now);
            // Phase 自動遷移：Impact フェーズは ImpactFlashSec を超えたら Fade に遷移して
            // ExpiresAt も Fade 完了時刻で上書きする（Confirmed の castTime+3s ベースの ExpiresAt
            // が Impact フラッシュより長ければそのまま、短ければ延長される）。
            foreach (var r in _active)
            {
                if (r.Phase == AoePhase.Impact)
                {
                    var elapsed = (now - r.PhaseStartedAt).TotalSeconds;
                    if (elapsed >= ImpactFlashSec)
                    {
                        r.Phase = AoePhase.Fade;
                        r.PhaseStartedAt = now;
                        r.ExpiresAt = now.AddSeconds(FadeDurationSec);
                    }
                }
            }
            regs = _active.ToArray();
        }
        if (regs.Length == 0)
        {
            return;
        }

        foreach (var reg in regs)
        {
            try
            {
                DrawOne(draw, gameGui, reg);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[FfxivEchoes] ActorTrackedAoe: 描画失敗 {Name} (id=0x{Id:X4})",
                    reg.CastName, reg.CastId);
            }
        }
    }

    /// <summary>距離カリング閾値（m）。プレイヤーから center までの 2D 距離がこれを超えたら描画 skip。</summary>
    /// <remarks>
    /// Splatoon の `LayoutUtils.ShouldDraw` 相当。アリーナワイドで多数 AoE が同時に立ったとき
    /// 画面外要素を WorldToScreen 前に CPU 側で間引いて 1ms/frame 予算を守る。
    /// 60m はアリーナ最大対角線（直径 50m + 余裕）想定。
    /// </remarks>
    public const float DrawDistanceCullM = 60f;

    private void DrawOne(ImDrawListPtr draw, IGameGui gameGui, AoeRegistration reg)
    {
        Vector3 center;
        Dalamud.Game.ClientState.Objects.Types.IGameObject? actor = null;

        // ── 中心座標：StaticWorldPos が優先 ─────────────
        if (reg.StaticWorldPos is { } staticPos)
        {
            // 静的 anchor（static / waymark）：actor 解決を skip
            center = staticPos;
        }
        else
        {
            // actor 追跡：毎フレーム ObjectTable から actor を引いて offset を加算
            actor = _objectTable.FindByEntityOrObjectId(reg.SourceId);
            if (actor is null)
            {
                // despawn したら描画しない（登録は ExpiresAt まで残す。
                // 一瞬だけ ObjectTable から消えるケースもあるので即時 RemoveAll はしない）
                return;
            }

            // alive ガード：HP=0 の actor は描画しない。
            // ObjectTable には死亡エフェクト中も残るので明示ガードが必要。
            if (actor is Dalamud.Game.ClientState.Objects.Types.IBattleNpc bnpc &&
                bnpc.CurrentHp == 0)
            {
                return;
            }

            // ターゲット中心 AoE 経路：FromCaster=false で TargetId 解決可なら target.Position 起点
            if (!reg.FromCaster && reg.TargetWorld is { } targetWorld)
            {
                center = new Vector3(
                    targetWorld.X + reg.ZoneOffsetX,
                    targetWorld.Y,
                    targetWorld.Z + reg.ZoneOffsetZ);
            }
            else if (!reg.FromCaster && reg.TargetId is { } tid && tid != 0 &&
                _objectTable.FindByEntityOrObjectId(tid) is { } tgt)
            {
                center = new Vector3(
                    tgt.Position.X + reg.ZoneOffsetX,
                    tgt.Position.Y,
                    tgt.Position.Z + reg.ZoneOffsetZ);
            }
            else
            {
                center = new Vector3(
                    actor.Position.X + reg.ZoneOffsetX,
                    actor.Position.Y,
                    actor.Position.Z + reg.ZoneOffsetZ);
            }
        }

        // ── 距離カリング ────────────────────────────
        // プレイヤーから center までの 2D 距離（XZ 平面）が DrawDistanceCullM を超えたら skip。
        // アリーナワイドや別フェーズの AoE が居残った場合に CPU を浪費しない。
        var localPlayer = _objectTable.LocalPlayer;
        if (localPlayer is not null)
        {
            var dx = center.X - localPlayer.Position.X;
            var dz = center.Z - localPlayer.Position.Z;
            // 半径分は遠くてもプレイヤーから見えるので、距離は (center 距離 - radius) で見る。
            var dist2D = MathF.Sqrt(dx * dx + dz * dz);
            if (dist2D - reg.Radius > DrawDistanceCullM)
            {
                return;
            }
        }

        // ── StateFilter 評価 ──────────────────────────
        if (reg.Filter is not null && !EvaluateStateFilter(reg.Filter, actor, reg))
        {
            return;
        }

        // rotation 解決 → 描画座標系へ変換。
        // actor=null（StaticWorldPos 経路）でも override / NorthAligned は計算可能。
        var ffxivRotation = ResolveRotation(reg, actor);
        var renderRotation = ApplyRenderRotationOffset(
            ConvertFfxivRotationToRender(ffxivRotation),
            reg.RotationOffsetRad);

        // 色生成：base 色 (#RRGGBB) を保持しつつ、alpha だけフェーズに応じて動的計算する。
        // Impact フェーズは赤強調なので base 色を上書き。
        // Predicted フェーズは PredictedFireAt を渡して「着弾時刻が近づくほど濃く」カウントダウン表現。
        var rgba = ParseColorAbgr(reg.ColorHex);
        var (fillAlpha, strokeAlpha) = ComputePhaseAlpha(
            reg.Phase, reg.PhaseStartedAt, DateTimeOffset.UtcNow, reg.PredictedFireAt);
        if (reg.Phase == AoePhase.Impact)
        {
            // 着弾フラッシュは強調赤（base 色を一時的に上書き）
            rgba = 0xFF3030F0u; // ABGR: alpha無視, B=0x30, G=0x30, R=0xF0
        }
        var fillColor = (rgba & 0x00FFFFFFu) | ((uint)fillAlpha << 24);
        var strokeColor = (rgba & 0x00FFFFFFu) | ((uint)strokeAlpha << 24);

        switch (reg.Shape)
        {
            case TrackedAoeShape.Circle:
                WorldShapeRenderer.DrawCircle(draw, gameGui, center, reg.Radius, fillColor, strokeColor);
                break;
            case TrackedAoeShape.Donut:
                WorldShapeRenderer.DrawDonut(draw, gameGui, center, reg.InnerRadius, reg.Radius, fillColor);
                break;
            case TrackedAoeShape.Cone:
            {
                var halfFan = reg.FanDeg * MathF.PI / 180f * 0.5f;
                WorldShapeRenderer.DrawCone(draw, gameGui, center, reg.Radius,
                    renderRotation - halfFan, renderRotation + halfFan, fillColor);
                break;
            }
            case TrackedAoeShape.Rect:
                WorldShapeRenderer.DrawRect(draw, gameGui, center, reg.Radius, reg.HalfWidth,
                    renderRotation, fillColor, strokeColor);
                break;
            case TrackedAoeShape.Cross:
                // halfLength = Radius、halfWidth = HalfWidth。Cross は中心起点。
                WorldShapeRenderer.DrawCross(draw, gameGui, center, reg.Radius, reg.HalfWidth,
                    renderRotation, fillColor, strokeColor);
                break;
            case TrackedAoeShape.DonutCone:
            {
                var halfFan = reg.FanDeg * MathF.PI / 180f * 0.5f;
                WorldShapeRenderer.DrawDonutCone(draw, gameGui, center,
                    reg.InnerRadius, reg.Radius,
                    renderRotation - halfFan, renderRotation + halfFan, fillColor);
                break;
            }
            case TrackedAoeShape.HalfPlane:
                // planeLength = Radius、halfWidth = HalfWidth（アリーナ半径相当を呼び元で詰める）
                WorldShapeRenderer.DrawHalfPlane(draw, gameGui, center,
                    renderRotation, reg.HalfWidth, reg.Radius, fillColor, strokeColor);
                break;
        }
    }

    /// <summary>
    /// rotation の取得元を <see cref="RotationKind"/> 指定に従って解決する。
    /// Splatoon の <c>GetRotationWithOverride</c> 相当。
    /// actor=null（静的位置）でも Override / NorthAligned は計算可能。
    /// </summary>
    private float ResolveRotation(AoeRegistration reg, Dalamud.Game.ClientState.Objects.Types.IGameObject? actor)
    {
        switch (reg.RotationKind)
        {
            case RotationKind.Override:
                // OverrideRotationRad は登録時に FFXIV rad 系に変換済
                return reg.OverrideRotationRad ?? (actor?.Rotation ?? 0f);

            case RotationKind.NorthAligned:
                // FFXIV: rotation π = 北
                return MathF.PI;

            case RotationKind.CastSnapshot:
                if (_snapshot.TryGet(reg.SourceId, reg.CastId, out var snap))
                {
                    return snap;
                }
                // 同 actor の最新 snapshot にフォールバック（cast id 違いでも actor 向きは
                // ほぼ同じはず）
                if (reg.SourceId != 0 &&
                    _snapshot.TryGetLatestForSource(reg.SourceId, out var latestSnap, out _))
                {
                    return latestSnap;
                }
                return actor?.Rotation ?? 0f;

            case RotationKind.Live:
                return actor?.Rotation ?? 0f;

            case RotationKind.TowardsTarget:
                if (actor is not null && reg.TargetId is { } tid &&
                    _objectTable.FindByEntityOrObjectId(tid) is { } tgt)
                {
                    var dx = tgt.Position.X - actor.Position.X;
                    var dz = tgt.Position.Z - actor.Position.Z;
                    // FFXIV rotation 体系（0=南=+Z、π/2=東、±π=北）に合わせる純粋関数。
                    return ComputeTowardsTargetRotation(dx, dz);
                }
                return actor?.Rotation ?? 0f;

            default:
                return actor?.Rotation ?? 0f;
        }
    }

    /// <summary>
    /// per-frame の表示判定。HP / 距離 / cast 中 / status 等を AND 評価。
    /// 返り値 true で描画する。
    /// </summary>
    private bool EvaluateStateFilter(
        StateFilter filter,
        Dalamud.Game.ClientState.Objects.Types.IGameObject? actor,
        AoeRegistration reg)
    {
        // HP 系：actor が IBattleChara/IBattleNpc 必須
        if (filter.HpPctMin is not null || filter.HpPctMax is not null)
        {
            if (actor is not Dalamud.Game.ClientState.Objects.Types.IBattleChara bc ||
                bc.MaxHp == 0)
            {
                return false;
            }
            var pct = (double)bc.CurrentHp / bc.MaxHp;
            if (filter.HpPctMin is { } min && pct < min) return false;
            if (filter.HpPctMax is { } max && pct > max) return false;
        }

        // 距離：プレイヤーから actor までの 2D 距離
        if (filter.DistanceMaxM is { } maxDist)
        {
            var lp = _objectTable.LocalPlayer;
            if (lp is null || actor is null) return false;
            var dx = actor.Position.X - lp.Position.X;
            var dz = actor.Position.Z - lp.Position.Z;
            if (MathF.Sqrt(dx * dx + dz * dz) > maxDist) return false;
        }

        // cast 中フラグ
        if (filter.OnlyWhileCasting == true)
        {
            if (actor is not Dalamud.Game.ClientState.Objects.Types.IBattleNpc bn || !bn.IsCasting)
            {
                return false;
            }
            if (filter.RequireCastId is { } reqCast && reqCast != 0 &&
                bn.CastActionId != reqCast)
            {
                return false;
            }
        }
        else if (filter.RequireCastId is { } reqCast2 && reqCast2 != 0)
        {
            if (actor is not Dalamud.Game.ClientState.Objects.Types.IBattleNpc bn2 ||
                !bn2.IsCasting || bn2.CastActionId != reqCast2)
            {
                return false;
            }
        }

        // status 持ち判定
        if (filter.RequireStatusId is { } reqStatus && reqStatus != 0)
        {
            if (actor is not Dalamud.Game.ClientState.Objects.Types.IBattleChara bc2)
            {
                return false;
            }
            var has = false;
            foreach (var st in bc2.StatusList)
            {
                if (st.StatusId == reqStatus) { has = true; break; }
            }
            if (!has) return false;
        }

        return true;
    }

    private bool IsFriendlyActor(uint id)
    {
        var obj = _objectTable.FindByEntityOrObjectId(id);
        // SearchById が null を返すケース（despawn 直後 / ゾーン遷移瞬間 / cross-server 等）は
        // 安全側に倒して friendly 扱い（AoE 登録しない）。
        // 旧実装は false で「敵」扱いし、PC スキル（コンフィテオル / ゴアブレード等）が
        // タイミングによって AoE 床塗りされて画面中央にゴミが残る原因になっていた。
        if (obj is null) return true;
        if (obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Pc) return true;
        // PC のペット / 召喚物（フェアリー / カーバンクル / クイーン 等）も friendly。
        // BattleNpc で OwnerId が PC を指していれば PC 召喚物。
        if (obj is Dalamud.Game.ClientState.Objects.Types.IBattleNpc bnpc && bnpc.OwnerId != 0)
        {
            var owner = _objectTable.FindByEntityOrObjectId(bnpc.OwnerId);
            if (owner is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter) return true;
        }
        return false;
    }

    /// <summary>Impact フェーズの赤フラッシュ持続時間（秒）。</summary>
    public const float ImpactFlashSec = 0.3f;
    /// <summary>Fade フェーズの線形減衰時間（秒）。</summary>
    public const float FadeDurationSec = 1.5f;
    /// <summary>Confirmed フェーズの sin パルス周期（秒）。</summary>
    public const float ConfirmedPulsePeriodSec = 1.2f;

    /// <summary>
    /// フェーズに応じた fill / stroke alpha を返す純粋関数。
    /// テスト・診断用に <c>public static</c>。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Predicted：薄い半透明（うっすら予告）</item>
    /// <item>Confirmed：標準濃度 + 周期パルス（cast 中の強調）</item>
    /// <item>Impact：最濃度（着弾フラッシュ、<see cref="ImpactFlashSec"/> 経過後 Fade 値へ自動降下）</item>
    /// <item>Fade：線形に 0 まで減衰</item>
    /// </list>
    /// </remarks>
    public static (byte FillAlpha, byte StrokeAlpha) ComputePhaseAlpha(
        AoePhase phase, DateTimeOffset phaseStartedAt, DateTimeOffset now,
        DateTimeOffset? predictedFireAt = null)
    {
        var t = (float)Math.Max(0, (now - phaseStartedAt).TotalSeconds);
        switch (phase)
        {
            case AoePhase.Predicted:
            {
                // 「録画ベース予測 AoE」を**事前に**しっかり認識できる濃度で表示。
                // 旧実装は (0x35, 0x80) = fill 21% / stroke 50% で「うっすら」過ぎ、
                // 「事前に AoE 範囲を知りたい」というユーザー要求を満たせていなかった。
                //
                // 新実装（カウントダウンアルファ）：着弾時刻が近づくほど濃くなる
                //   - 8s 以上前: fill 0x55（33% — 控えめだが識別可能）
                //   - 5〜8s 前: fill 0x55 → 0x70 へ漸増
                //   - 2〜5s 前: fill 0x70 → 0x90 へ漸増（Confirmed に近づく）
                //   - 0〜2s 前: fill 0x90 → 0xA0（緊急感、Impact 直前）
                // stroke も 0xC0 → 0xFF へカウントダウン。
                // PredictedFireAt が無いケース（フォールバック）は固定 (0x55, 0xC0)。
                if (predictedFireAt is { } fireAt)
                {
                    var secToFire = (float)Math.Max(0, (fireAt - now).TotalSeconds);
                    byte fill;
                    byte stroke;
                    if (secToFire > 8f)
                    {
                        fill = 0x55; stroke = 0xC0;
                    }
                    else if (secToFire > 5f)
                    {
                        var r = (8f - secToFire) / 3f; // 0..1
                        fill = (byte)(0x55 + r * (0x70 - 0x55));
                        stroke = 0xC0;
                    }
                    else if (secToFire > 2f)
                    {
                        var r = (5f - secToFire) / 3f;
                        fill = (byte)(0x70 + r * (0x90 - 0x70));
                        stroke = (byte)(0xC0 + r * (0xE0 - 0xC0));
                    }
                    else
                    {
                        var r = Math.Clamp((2f - secToFire) / 2f, 0f, 1f);
                        fill = (byte)(0x90 + r * (0xA0 - 0x90));
                        stroke = 0xFF;
                    }
                    return (fill, stroke);
                }
                return (0x55, 0xC0);
            }

            case AoePhase.Confirmed:
            {
                // キャスト開始 = 着弾の数秒前。ここでも「事前」だがより確信度が高いので、
                // Predicted より濃く、Impact より薄い中間濃度で表示。
                //   t=0 で即座にピーク（0xB0）到達 → 0.4s で base 0x90 に落ち着き、sin パルス継続。
                //   旧実装の base 0x60 + ±0x10（24-31%）は薄すぎて遅延感の原因だった。
                const float SnapInSec = 0.4f;
                var snapBoost = t < SnapInSec ? (1f - t / SnapInSec) * 0x20 : 0f;
                var sin01 = (MathF.Sin(t * MathF.Tau / ConfirmedPulsePeriodSec) + 1f) * 0.5f;
                var fill = (byte)Math.Clamp(0x90 + (int)snapBoost + (int)(sin01 * 0x18), 0x70, 0xC0);
                return (fill, 0xFF);
            }

            case AoePhase.Impact:
            {
                if (t < ImpactFlashSec)
                {
                    return (0x90, 0xFF); // ピーク
                }
                // 経過時間が ImpactFlashSec を超えた場合は Fade 計算と同等の挙動を返す。
                // 実際には DrawAll の Phase 自動遷移ロジックでこの分岐に来る前に Phase=Fade に
                // 書き換わるが、安全側で fade 値も計算しておく。
                var fadeT = t - ImpactFlashSec;
                var ratio = Math.Clamp(1f - fadeT / FadeDurationSec, 0f, 1f);
                return ((byte)(0x60 * ratio), (byte)(0xE0 * ratio));
            }

            case AoePhase.Fade:
            {
                var ratio = Math.Clamp(1f - t / FadeDurationSec, 0f, 1f);
                return ((byte)(0x60 * ratio), (byte)(0xE0 * ratio));
            }

            default:
                return (0x60, 0xE0);
        }
    }

    /// <summary>
    /// "#RRGGBB" → ABGR uint。<see cref="WorldOverlayWindow.ParseColor"/> と同じ式。
    /// </summary>
    private static uint ParseColorAbgr(string colorHex)
    {
        if (string.IsNullOrEmpty(colorHex) || colorHex.Length != 7 || colorHex[0] != '#')
        {
            return 0xFF6464FFu; // 既定：赤
        }
        if (!uint.TryParse(colorHex.AsSpan(1), System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var rgb))
        {
            return 0xFF6464FFu;
        }
        var r = (rgb >> 16) & 0xFF;
        var g = (rgb >> 8) & 0xFF;
        var b = rgb & 0xFF;
        return (0xFFu << 24) | (b << 16) | (g << 8) | r;
    }

    /// <summary>
    /// 描画する AoE shape の種別。<see cref="StrategyAoeZone.Shape"/> の文字列より
    /// 高速・型安全な内部表現。
    /// </summary>
    public enum TrackedAoeShape
    {
        Circle,
        Donut,
        Cone,
        Rect,
        /// <summary>十字（縦横 2 本の rect 重ね）</summary>
        Cross,
        /// <summary>扇ドーナツ（cone の中央 inner_radius を切り抜き）</summary>
        DonutCone,
        /// <summary>半面攻撃（actor から片側に伸びる長 rect）</summary>
        HalfPlane,
    }

    /// <summary>
    /// 登録元の経路。Lumina 自動推測か、ユーザー定義の攻略経由か、録画予測由来か。
    /// dedup と suppress 制御、フェーズ遷移の識別に使う。
    /// </summary>
    public enum AoeRegistrationSource
    {
        AutoLumina,
        UserDefined,
        /// <summary>録画予測由来。CastStarted で Confirmed に昇格、来なければ自動消滅。</summary>
        Predicted,
    }

    /// <summary>
    /// 4 段階フェーズ：予告（録画予測ベース・薄い表示） → 確定（cast 開始・パルス強調）
    /// → 着弾（cast 完了・赤フラッシュ） → 余韻（フェードアウト）。
    /// 「avoid 時間が無い」体感を「予告から段階的に出る」表示で軽減する。
    /// </summary>
    public enum AoePhase
    {
        Predicted,
        Confirmed,
        Impact,
        Fade,
    }

    /// <summary>
    /// 1 件の追跡対象 AoE。<see cref="ActorTrackedAoeService"/> 内部だけで使う runtime 状態。
    /// JSON シリアライズ用 POCO ではない（マッチャー定義は <see cref="ActorMatcher"/>）。
    /// </summary>
    public sealed class AoeRegistration
    {
        public uint SourceId { get; init; }
        public uint CastId { get; init; }
        public string CastName { get; init; } = string.Empty;
        public TrackedAoeShape Shape { get; init; }
        public float Radius { get; init; }
        public float InnerRadius { get; init; }
        public float FanDeg { get; init; }
        public float HalfWidth { get; init; }

        /// <summary>
        /// true = AoE はキャスター位置（CastType 5/3/4/6 等）。
        /// false = ターゲット位置（CastType 2、ground-targeted）。
        /// </summary>
        public bool FromCaster { get; init; }

        /// <summary>ターゲット中心 AoE の場合の対象 id。</summary>
        public uint? TargetId { get; init; }

        /// <summary>ActionEffect から得られたターゲット/地面座標。TargetId より優先して中心に使う。</summary>
        public Vector3? TargetWorld { get; init; }

        public RotationKind RotationKind { get; init; }

        /// <summary><see cref="RotationKind.Override"/> 時に使う角度（FFXIV 系ラジアン）。</summary>
        public float? OverrideRotationRad { get; init; }

        /// <summary>描画角度に最後に足すオフセット（描画系ラジアン）。</summary>
        public float RotationOffsetRad { get; init; }

        /// <summary>"#RRGGBB" 形式。</summary>
        public string ColorHex { get; init; } = "#FF6464";

        /// <summary>これを過ぎたフレームで自動消滅。</summary>
        public DateTimeOffset ExpiresAt { get; set; }

        /// <summary>登録経路。dedup と suppress_auto_aoe 制御に使う。</summary>
        public AoeRegistrationSource Source { get; init; } = AoeRegistrationSource.AutoLumina;

        /// <summary>
        /// 表示フェーズ（Predicted / Confirmed / Impact / Fade）。
        /// 遷移は <see cref="ActorTrackedAoeService"/> 内の lock (_gate) ブロック内でのみ書き換える。
        /// </summary>
        public AoePhase Phase { get; set; } = AoePhase.Confirmed;

        /// <summary>現フェーズに入った時刻。アニメーション計算の基点。常に <c>DateTimeOffset.UtcNow</c> ベース。</summary>
        public DateTimeOffset PhaseStartedAt { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>Predicted フェーズで「発動予定」とみなす時刻（録画予測由来）。</summary>
        public DateTimeOffset? PredictedFireAt { get; init; }

        /// <summary>UserDefined 経路で StrategyAoeZone.Id を保持。同 zone の dedup キー。</summary>
        public string? ZoneId { get; init; }

        /// <summary>UserDefined 経路で actor を絞り込むマッチャー。</summary>
        public ActorMatcher? Matcher { get; init; }

        /// <summary>表示するかの per-frame 判定（HP / 距離 / cast 中等）。</summary>
        public StateFilter? Filter { get; init; }

        /// <summary>HalfPlane 用：アリーナ半径（m）。Radius / HalfWidth 算出に使う。</summary>
        public float ArenaRadiusHint { get; init; }

        /// <summary>静的 anchor のとき固定世界座標を保持（actor 解決を skip）。</summary>
        public Vector3? StaticWorldPos { get; init; }

        /// <summary>actor 追跡時、zone の (X, Z) を actor.Position に加算するためのオフセット。</summary>
        public float ZoneOffsetX { get; init; }
        public float ZoneOffsetZ { get; init; }

        /// <summary>同 cast id の AutoLumina エントリを抑止する（user_defined &amp; suppress = true）。</summary>
        public bool SuppressAutoLumina { get; init; }
    }
}
