using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FfxivEchoes.Capture;
using FfxivEchoes.Events;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Triggers;

/// <summary>
/// メカニクスを「複数種の発動条件」で起動する横断サービス。
/// 旧 AutoTelegraphService が AoE 自動描画と一緒にやっていた "cast → strategy fire" 部分を
/// AoE 描画から切り離した版。同時に status_gain / status_lose / rotation トリガーも扱う。
/// </summary>
/// <remarks>
/// 発動条件の評価は <see cref="MechanicStrategy.Triggers"/> を OR 結合で見る。
/// 空のときは旧 <see cref="MechanicStrategy.AttachedTo"/>（cast 想定）にフォールバック。
/// rotation トリガーは Framework.Update で対象アクターの向きをポーリングする。
/// </remarks>
public sealed class MechanicTriggerService : IDisposable
{
    private const double ObjectGroupStabilityDelaySec = 0.35;

    private readonly IFramework _framework;
    private readonly IEventBus _bus;
    private readonly TriggerStore _store;
    private readonly IObjectTable _objectTable;
    private readonly IPluginLog _log;
    private readonly Func<string?, bool>? _branchActiveCheck;

    private readonly IDisposable _castSub;
    private readonly IDisposable _statusGainSub;
    private readonly IDisposable _statusLoseSub;
    private readonly IDisposable _actionSub;
    private readonly IDisposable _hpSub;
    private readonly IDisposable _objectAppearSub;
    private readonly IDisposable _zoneSub;
    private readonly IDisposable _combatStartSub;
    private readonly IDisposable _combatEndSub;

    private string _currentZone = "Unknown";
    /// <summary>戦闘中フラグ。戦闘外でメカニクスは発火しない（pre-pull 騒音を防ぐ）。</summary>
    private bool _inCombat;
    private readonly object _gate = new();

    /// <summary>(profileId, mechanicId) → 最後の発動時刻。dedup 用。</summary>
    private readonly Dictionary<string, DateTimeOffset> _lastFiredAt = new();
    /// <summary>rotation 監視で「すでに範囲内に居る」状態を覚えて再発火防止。</summary>
    private readonly Dictionary<string, bool> _rotationInside = new();
    /// <summary>HP% トリガー：actorId 毎の前回観測 HP%。閾値跨ぎ検知用。</summary>
    private readonly Dictionary<uint, float> _prevHpPctByActor = new();
    private readonly List<ObservedObject> _recentObjects = new();

    public MechanicTriggerService(
        IFramework framework,
        IEventBus bus,
        TriggerStore store,
        IObjectTable objectTable,
        IPluginLog log,
        Func<string?, bool>? branchActiveCheck = null)
    {
        _framework = framework;
        _bus = bus;
        _store = store;
        _objectTable = objectTable;
        _log = log;
        _branchActiveCheck = branchActiveCheck;

        _castSub = bus.Subscribe<CastStartedEvent>(OnCastStart);
        _actionSub = bus.Subscribe<ActionUsedEvent>(OnActionUsed);
        _statusGainSub = bus.Subscribe<StatusGainedEvent>(OnStatusGained);
        _statusLoseSub = bus.Subscribe<StatusLostEvent>(OnStatusLost);
        _hpSub = bus.Subscribe<HpChangedEvent>(OnHpChanged);
        _objectAppearSub = bus.Subscribe<ObjectAppearedEvent>(OnObjectAppeared);
        _zoneSub = bus.Subscribe<ZoneChangedEvent>(z =>
        {
            _currentZone = string.IsNullOrEmpty(z.ZoneName) ? "Unknown" : z.ZoneName;
            _inCombat = false; // ゾーン跨ぎは強制 pre-combat
            lock (_gate)
            {
                _lastFiredAt.Clear();
                _rotationInside.Clear();
                _prevHpPctByActor.Clear();
                _recentObjects.Clear();
            }
        });
        // ワイプ→同ゾーン再挑戦に備え、戦闘開始/終了の両方で per-combat 状態をクリアする。
        // ZoneChangedEvent でしかクリアしないと、絶コンテンツの多数回ワイプで前回戦闘の
        // _prevHpPctByActor が残り hp_pct フェーズトリガーが誤発火/不発し、_lastFiredAt 残留で
        // 開幕の正規発火が dedup に巻き込まれて沈黙する。
        _combatStartSub = bus.Subscribe<CombatStartedEvent>(_ =>
        {
            _inCombat = true;
            lock (_gate)
            {
                _lastFiredAt.Clear();
                _rotationInside.Clear();
                _prevHpPctByActor.Clear();
                _recentObjects.Clear();
            }
        });
        _combatEndSub = bus.Subscribe<CombatEndedEvent>(_ =>
        {
            _inCombat = false;
            lock (_gate)
            {
                _lastFiredAt.Clear();
                _rotationInside.Clear();
                _prevHpPctByActor.Clear();
                _recentObjects.Clear();
            }
        });

        _framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        _framework.Update -= OnUpdate;
        _castSub.Dispose();
        _actionSub.Dispose();
        _statusGainSub.Dispose();
        _statusLoseSub.Dispose();
        _hpSub.Dispose();
        _objectAppearSub.Dispose();
        _zoneSub.Dispose();
        _combatStartSub.Dispose();
        _combatEndSub.Dispose();
    }

    private TriggerFile? GetActiveFile()
    {
        var file = _store.GetByZone(_currentZone);
        return (file is null || !file.AutoSettings.EnableTriggers) ? null : file;
    }

    private void OnCastStart(CastStartedEvent ev)
    {
        var file = GetActiveFile();
        if (file is null) return;

        // 旧パス：AttachedTo ベースで合致したメカニクスを発動
        var legacy = StrategyPlanResolver.FindMechanicForCast(
            file,
            ev.CastActionId,
            ev.CastActionName,
            _branchActiveCheck);
        if (legacy.Profile is not null && legacy.Mechanic is not null &&
            HasNoExplicitTriggers(legacy.Mechanic) &&
            IsBranchAllowed(legacy.Mechanic))
        {
            FireMechanic(legacy.Profile, legacy.Mechanic, ev);
        }

        // 新パス：mechanic.Triggers の cast / action_used を見る
        EvaluateTriggers(file, t =>
            (t.Type == "cast" || t.Type == "action_used") &&
            MatchesCast(t.Match, ev),
            ev);
    }

    private void OnActionUsed(ActionUsedEvent ev)
    {
        var file = GetActiveFile();
        if (file is null) return;
        EvaluateTriggers(file, t =>
            t.Type == "action_used" && MatchesAction(t.Match, ev),
            ev);
    }

    private void OnStatusGained(StatusGainedEvent ev)
    {
        var file = GetActiveFile();
        if (file is null) return;
        EvaluateTriggers(file, t =>
            t.Type == "status_gain" && MatchesStatus(t.Match, ev.StatusId, ev.StatusName),
            ev);
    }

    private void OnStatusLost(StatusLostEvent ev)
    {
        var file = GetActiveFile();
        if (file is null) return;
        EvaluateTriggers(file, t =>
            t.Type == "status_lose" && MatchesStatus(t.Match, ev.StatusId, ev.StatusName),
            ev);
    }

    private void OnObjectAppeared(ObjectAppearedEvent ev)
    {
        var file = GetActiveFile();
        if (file is null) return;

        lock (_gate)
        {
            _recentObjects.Add(new ObservedObject(ev.ObjectName, ev.DataId, ev.Position, ev.Timestamp));
            _recentObjects.RemoveAll(o => (ev.Timestamp - o.Timestamp).TotalSeconds > 10.0);
        }

        EvaluateTriggers(file, t =>
            t.Type == "object_appear" && MatchesObject(t, ev.ObjectName, ev.DataId),
            ev);
    }

    /// <summary>
    /// HP% トリガー：actor 名 / DataId が一致する HpChangedEvent に対し、
    /// 前回観測 HP% との比較で「閾値を跨いだ」場合だけ発動する（連続発火防止）。
    /// </summary>
    private void OnHpChanged(HpChangedEvent ev)
    {
        // hp_pct トリガーはボスフェーズ移行用（80%, 50%, 25% 等）。PT メンバー HP は対象外。
        // PT 8 人が同時に被弾すると 8 回フルスキャンが走り、誤マッチで PT 名→ボス名類推が
        // 動く可能性もあるため、入口で確実に skip する。
        if (ev.IsPlayer) return;

        var file = GetActiveFile();
        if (file is null) return;

        float prev;
        lock (_gate)
        {
            prev = _prevHpPctByActor.TryGetValue(ev.ActorId, out var p) ? p : float.NaN;
            _prevHpPctByActor[ev.ActorId] = ev.HpPct;
        }
        if (float.IsNaN(prev)) return; // 初回観測は判定しない

        foreach (var profile in file.StrategyProfiles)
        {
            if (!profile.Enabled) continue;
            foreach (var mech in profile.Mechanics)
            {
                    if (!mech.Enabled || !IsBranchAllowed(mech)) continue;
                foreach (var trig in mech.Triggers)
                {
                    if (trig.Type != "hp_pct") continue;
                    if (!MatchesActorByName(trig, ev.ActorName, 0)) continue;

                    var crossedBelow = trig.HpPctBelow is { } below
                        && PhaseTransitionPolicy.CrossedBelow(prev, ev.HpPct, (float)below);
                    var crossedAbove = trig.HpPctAbove is { } above
                        && prev <= above && ev.HpPct > above;
                    if (!crossedBelow && !crossedAbove) continue;

                    // フェーズ境界の hp_pct トリガー（phase 設定あり）なら、下方跨ぎで
                    // フェーズ遷移を通知する（sync_point を使わず HP% で管理するコンテンツ向け）。
                    if (crossedBelow && !string.IsNullOrEmpty(trig.Phase))
                    {
                        _bus.Publish(new PhaseTransitionedEvent(DateTimeOffset.UtcNow, trig.Phase!));
                    }

                    var dedup = trig.DedupSec ?? 5.0;
                    if (CheckAndStampFire(profile, mech, DateTimeOffset.UtcNow, dedup, $"hp:{ev.ActorId}"))
                    {
                        _log.Information(
                            "[FfxivEchoes] hp_pct 発動: {Mech} ({Actor} {Prev}% → {Now}%)",
                            mech.Label, ev.ActorName, prev, ev.HpPct);
                        FireMechanic(profile, mech, ev);
                    }
                    break;
                }
            }
        }
    }

    private static bool MatchesActorByName(MechanicTrigger trig, string actorName, uint dataId)
    {
        if (trig.ActorDataId is { } did && did != 0)
        {
            // dataId が分かるイベントなら厳密一致。HpChangedEvent は dataId を持たないので
            // 名前で代用するしかない場合がある。
            if (dataId != 0 && dataId != did) return false;
        }
        if (!string.IsNullOrEmpty(trig.ActorName))
        {
            if (string.IsNullOrEmpty(actorName)) return false;
            if (!actorName.Contains(trig.ActorName, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static bool MatchesObject(MechanicTrigger trig, string objectName, uint dataId)
    {
        if (trig.ActorDataId is { } did && did != 0 && dataId != did)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(trig.ActorName))
        {
            if (string.IsNullOrEmpty(objectName)) return false;
            if (!objectName.Contains(trig.ActorName, StringComparison.OrdinalIgnoreCase)) return false;
        }

        if (trig.Match is { } match)
        {
            if (match.SourceId is { } sid && sid != 0 && dataId != sid) return false;
            if (!string.IsNullOrEmpty(match.Actor) &&
                !objectName.Contains(match.Actor, StringComparison.OrdinalIgnoreCase)) return false;
        }

        return trig.ActorDataId is not null ||
               !string.IsNullOrEmpty(trig.ActorName) ||
               trig.Match is not null;
    }

    private void EvaluateObjectGroupTriggers(TriggerFile file, DateTimeOffset now)
    {
        foreach (var profile in file.StrategyProfiles)
        {
            if (!profile.Enabled) continue;
            foreach (var mech in profile.Mechanics)
            {
                if (!mech.Enabled || !IsBranchAllowed(mech)) continue;
                foreach (var trig in mech.Triggers)
                {
                    if (trig.Type != "object_group") continue;

                    var windowSec = Math.Max(0.1, trig.ObjectWindowSec ?? 1.5);
                    ObservedObject[] matches;
                    lock (_gate)
                    {
                        matches = _recentObjects
                            .Where(o => (now - o.Timestamp).TotalSeconds <= windowSec)
                            .Where(o => MatchesObject(trig, o.Name, o.DataId))
                            .ToArray();
                    }

                    if (matches.Length == 0) continue;
                    var newest = matches.MaxBy(o => o.Timestamp);
                    var oldest = matches.MinBy(o => o.Timestamp);
                    var count = matches.Length;
                    if (!ShouldFireObjectGroup(
                            count,
                            trig.ObjectCountMin,
                            trig.ObjectCountMax,
                            (now - newest.Timestamp).TotalSeconds,
                            (now - oldest.Timestamp).TotalSeconds,
                            windowSec))
                    {
                        continue;
                    }

                    var dedup = trig.DedupSec ?? 5.0;
                    if (!CheckAndStampFire(profile, mech, now, dedup, BuildObjectGroupDedupeKey(matches)))
                    {
                        continue;
                    }

                    var source = new ObjectGroupAppearedEvent(
                        Timestamp: newest.Timestamp,
                        ObjectName: newest.Name,
                        DataId: newest.DataId,
                        Count: count,
                        Positions: matches.Select(m => m.Position).ToArray());
                    FireMechanic(profile, mech, source);
                    break;
                }
            }
        }
    }

    private void OnUpdate(IFramework _)
    {
        // IFramework.Update に直結するため、例外が出ると Dalamud フレームループに伝播し
        // 全プラグインを巻き込む。coding-rules.md に従いトップレベルで全捕捉する。
        // ObjectTable 走査（ResolveActorByRotationTrigger / EvaluateObjectGroupTriggers）は
        // 絶コンテンツの大量 add 出現/消滅でアクター解放と重なると例外を投げうる。
        try
        {
            OnUpdateCore();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[FfxivEchoes] MechanicTriggerService.OnUpdate 例外");
        }
    }

    private void OnUpdateCore()
    {
        var file = GetActiveFile();
        if (file is null) return;

        var now = DateTimeOffset.UtcNow;
        EvaluateObjectGroupTriggers(file, now);

        foreach (var profile in file.StrategyProfiles)
        {
            if (!profile.Enabled) continue;
            foreach (var mech in profile.Mechanics)
            {
                if (!mech.Enabled || !IsBranchAllowed(mech)) continue;
                foreach (var trig in mech.Triggers)
                {
                    if (trig.Type != "rotation") continue;
                    if (!trig.FacingDeg.HasValue) continue;

                    var actor = ResolveActorByRotationTrigger(trig);
                    if (actor is null) continue;

                    var actorAngleDeg = NormalizeDeg(actor.Rotation * (180.0 / Math.PI));
                    var targetDeg = NormalizeDeg(trig.FacingDeg.Value);
                    var tol = trig.FacingToleranceDeg ?? 30.0;
                    var inside = AngleDistance(actorAngleDeg, targetDeg) <= tol;

                    var key = $"rot:{profile.Id}::{mech.Id}";
                    bool prev;
                    lock (_gate) { _rotationInside.TryGetValue(key, out prev); _rotationInside[key] = inside; }

                    // 範囲外 → 範囲内に変わった瞬間だけ発火（連続発火防止）
                    if (inside && !prev)
                    {
                        var dedup = trig.DedupSec ?? 5.0;
                        if (CheckAndStampFire(profile, mech, now, dedup, $"rot:{actor.EntityId}"))
                        {
                            _log.Information(
                                "[FfxivEchoes] MechanicTrigger rotation 発動: {Mech} ({Actor} → {Deg}°)",
                                mech.Label, actor.Name?.TextValue ?? "?", actorAngleDeg);
                            // rotation 発動には対応する実イベントが無いので CombatStartedEvent を仮装する
                            FireMechanic(profile, mech, new CombatStartedEvent(now));
                        }
                    }
                }
            }
        }
    }

    /// <summary>明示 Triggers を持たないメカニクスのみ legacy パスで撃つ判定。</summary>
    private static bool HasNoExplicitTriggers(MechanicStrategy m) => m.Triggers.Count == 0;

    private void EvaluateTriggers(TriggerFile file, Func<MechanicTrigger, bool> predicate, IGameEvent source)
    {
        foreach (var profile in file.StrategyProfiles)
        {
            if (!profile.Enabled) continue;
            foreach (var mech in profile.Mechanics)
            {
                if (!mech.Enabled || !IsBranchAllowed(mech)) continue;
                if (mech.Triggers.Count == 0) continue;
                foreach (var trig in mech.Triggers)
                {
                    if (!predicate(trig)) continue;
                    var dedup = trig.DedupSec ?? 1.0;
                    if (CheckAndStampFire(profile, mech, DateTimeOffset.UtcNow, dedup, BuildEventDedupeKey(source)))
                    {
                        FireMechanic(profile, mech, source);
                    }
                    break; // メカニクス内では 1 つ通れば十分
                }
            }
        }
    }

    private bool CheckAndStampFire(
        StrategyProfile profile,
        MechanicStrategy mech,
        DateTimeOffset now,
        double dedupSec,
        string? sourceKey = null)
    {
        var key = BuildFireDedupeKey(profile.Id, mech.Id, sourceKey);
        lock (_gate)
        {
            if (_lastFiredAt.TryGetValue(key, out var prev) &&
                (now - prev).TotalSeconds < dedupSec)
            {
                return false;
            }
            _lastFiredAt[key] = now;
        }
        return true;
    }

    public static string BuildFireDedupeKey(string profileId, string mechanicId, string? sourceKey)
        => string.IsNullOrWhiteSpace(sourceKey)
            ? $"{profileId}::{mechanicId}::global"
            : $"{profileId}::{mechanicId}::{sourceKey}";

    public static bool ShouldFireObjectGroup(
        int count,
        int? minCount,
        int? maxCount,
        double secondsSinceLastAdd,
        double secondsSinceOldest,
        double windowSec,
        double stabilityDelaySec = ObjectGroupStabilityDelaySec)
    {
        if (minCount is { } min && count < min) return false;
        if (maxCount is { } max && count > max) return false;
        if (minCount is null && count == 0) return false;
        if (secondsSinceLastAdd < stabilityDelaySec) return false;
        if (maxCount is { } expectedMax && count < expectedMax && secondsSinceOldest < windowSec)
        {
            return false;
        }
        return true;
    }

    private bool IsBranchAllowed(MechanicStrategy mech)
        => _branchActiveCheck?.Invoke(mech.BranchId) ?? true;

    private static string? BuildEventDedupeKey(IGameEvent source) => source switch
    {
        CastStartedEvent x => $"cast:{x.SourceId}",
        ActionUsedEvent x => $"action:{x.SourceId}:{x.TargetId ?? 0}",
        StatusGainedEvent x => $"status_gain:{x.SourceId}:{x.TargetId}",
        StatusLostEvent x => $"status_lose:{x.TargetId}",
        HpChangedEvent x => $"hp:{x.ActorId}",
        ObjectAppearedEvent x => $"object:{x.ObjectId}:{x.DataId}",
        ObjectGroupAppearedEvent x => BuildObjectGroupDedupeKey(
            x.Positions.Select(p => new ObservedObject(x.ObjectName, x.DataId, p, x.Timestamp))),
        _ => null,
    };

    private static string BuildObjectGroupDedupeKey(IEnumerable<ObservedObject> matches)
    {
        var parts = matches
            .OrderBy(m => m.DataId)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Position.X)
            .ThenBy(m => m.Position.Z)
            .Select(m => $"{m.DataId}:{m.Name}:{m.Position.X:0.0}:{m.Position.Z:0.0}");
        return "object_group:" + string.Join("|", parts);
    }

    private void FireMechanic(StrategyProfile profile, MechanicStrategy mech, IGameEvent source)
    {
        if (!IsBranchAllowed(mech))
        {
            return;
        }

        // 戦闘外ではメカニクスを発火しない。pre-pull の不要な発火（zone 入った瞬間に
        // 周囲の add NPC で object_group がマッチして AoE が出る等）を防ぐ。
        // 例外：source 自体が CombatStartedEvent なら通す（戦闘開始の瞬間にも発火させたい）。
        if (!_inCombat && source is not CombatStartedEvent)
        {
            return;
        }

        var actions = StrategyPlanResolver.BuildReminderActions(_store.GetByZone(_currentZone), profile, mech);
        if (actions.Count == 0) return;
        _bus.Publish(new TriggerFiredEvent(
            Timestamp: DateTimeOffset.UtcNow,
            Zone: _currentZone,
            TriggerId: $"__strategy_{profile.Id}_{mech.Id}",
            TriggerName: mech.Label,
            Actions: actions,
            SourceEvent: source));
    }

    // ── マッチ評価 ───────────────────────────────────────

    private static bool MatchesCast(MatchCondition? m, CastStartedEvent ev)
    {
        if (m is null) return false;
        if (!string.IsNullOrEmpty(m.CastId))
        {
            if (!TryParseHex(m.CastId, out var id)) return false;
            if (id != ev.CastActionId) return false;
        }
        if (!string.IsNullOrEmpty(m.CastName) &&
            !ev.CastActionName.Contains(m.CastName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return true;
    }

    private static bool MatchesAction(MatchCondition? m, ActionUsedEvent ev)
    {
        if (m is null) return false;
        if (!string.IsNullOrEmpty(m.ActionId))
        {
            if (!TryParseHex(m.ActionId, out var id)) return false;
            if (id != ev.ActionId) return false;
        }
        if (!string.IsNullOrEmpty(m.ActionName) &&
            !ev.ActionName.Contains(m.ActionName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return true;
    }

    private static bool MatchesStatus(MatchCondition? m, uint statusId, string statusName)
    {
        if (m is null) return false;
        if (m.StatusId is { } sid && sid != statusId) return false;
        if (!string.IsNullOrEmpty(m.StatusName) &&
            !statusName.Contains(m.StatusName, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static bool TryParseHex(string s, out uint id)
    {
        id = 0;
        var x = s.Trim();
        if (x.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) x = x[2..];
        return uint.TryParse(x, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out id);
    }

    private IGameObject? ResolveActorByRotationTrigger(MechanicTrigger t)
    {
        try
        {
            foreach (var obj in _objectTable)
            {
                // BaseId / Name をベースに対象アクターを絞り込む。HP 持ちの戦闘体だけが対象
                if (t.ActorDataId is { } did && obj.BaseId != did) continue;
                if (obj is IBattleChara bc && bc.MaxHp == 0) continue;
                if (!string.IsNullOrEmpty(t.ActorName))
                {
                    var n = obj.Name.TextValue;
                    if (string.IsNullOrEmpty(n) ||
                        !n.Contains(t.ActorName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }
                return obj;
            }
        }
        catch (Exception ex)
        {
            // ObjectTable 列挙中にアクターが解放されると例外になりうる。握り潰さずログは残す。
            _log.Debug(ex, "[FfxivEchoes] rotation actor 解決中の列挙例外");
        }
        return null;
    }

    private static double NormalizeDeg(double d)
    {
        d %= 360.0;
        if (d > 180) d -= 360;
        if (d < -180) d += 360;
        return d;
    }

    private static double AngleDistance(double a, double b)
    {
        var d = Math.Abs(NormalizeDeg(a - b));
        return d > 180 ? 360 - d : d;
    }

    private readonly record struct ObservedObject(string Name, uint DataId, System.Numerics.Vector3 Position, DateTimeOffset Timestamp);
}
