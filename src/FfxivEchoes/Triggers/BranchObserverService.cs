using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FfxivEchoes.Capture;
using FfxivEchoes.Events;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Triggers;

/// <summary>
/// タイムライン分岐の判定サービス。戦闘開始時にゾーンの <see cref="TriggerFile.Branches"/>
/// を読み込み、判定対象イベント（cast / status / object）を観測して各 branch を
/// <see cref="BranchStatus.Active"/> または <see cref="BranchStatus.Rejected"/> に遷移させる。
/// </summary>
/// <remarks>
/// <para>
/// 「最初に来たキャスト次第でタイムラインがパターン分岐する」絶・滅コンテンツ用。
/// 例：滅エヌオーで「エアロジャ → パターン1」「フレア → パターン2」。
/// </para>
/// <para>
/// 状態は per-zone でメモリのみ。<see cref="ZoneChangedEvent"/> / <see cref="CombatEndedEvent"/>
/// でクリア。<see cref="CombatStartedEvent"/> で再初期化。
/// </para>
/// <para>
/// 状態変化時は <see cref="BranchResolvedEvent"/> を bus に publish し、UI 側
/// （<see cref="FfxivEchoes.Windows.UpcomingEventsWindow"/> / <see cref="PredictedCastReminderService"/>）
/// がキャッシュを invalidate して再描画する。
/// </para>
/// </remarks>
public sealed class BranchObserverService : IDisposable
{
    private readonly IEventBus _bus;
    private readonly TriggerStore _store;
    private readonly CombatClock _combatClock;
    private readonly IPluginLog _log;

    private readonly IDisposable _combatStartSub;
    private readonly IDisposable _combatEndSub;
    private readonly IDisposable _zoneSub;
    private readonly IDisposable _castStartSub;
    private readonly IDisposable _statusSub;
    private readonly IDisposable _objectSub;
    private readonly IDisposable _phaseSub;

    private readonly Dictionary<string, BranchStatus> _statuses = new(StringComparer.Ordinal);
    // 分岐グループ（group_id）ごとの判定ウィンドウ起点。戦闘開始で初期化し、フェーズ遷移で
    // リセットする。これが無いと後半フェーズの分岐グループも戦闘開始からの elapsed で
    // window_sec 判定され、判定キャストが来る頃には超過して永久 Pending になる（FIX-10）。
    private readonly Dictionary<string, DateTimeOffset> _groupActivatedAt = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private string _currentZone = "Unknown";
    private DateTimeOffset? _combatStartedAt;

    public BranchObserverService(IEventBus bus, TriggerStore store, CombatClock combatClock, IPluginLog log)
    {
        _bus = bus;
        _store = store;
        _combatClock = combatClock;
        _log = log;

        _combatStartSub = bus.Subscribe<CombatStartedEvent>(OnCombatStarted);
        _combatEndSub = bus.Subscribe<CombatEndedEvent>(_ => Reset());
        _zoneSub = bus.Subscribe<ZoneChangedEvent>(z =>
        {
            _currentZone = string.IsNullOrEmpty(z.ZoneName) ? "Unknown" : z.ZoneName;
            Reset();
        });
        _castStartSub = bus.Subscribe<CastStartedEvent>(OnCastStarted);
        _statusSub = bus.Subscribe<StatusGainedEvent>(OnStatusGained);
        _objectSub = bus.Subscribe<ObjectAppearedEvent>(OnObjectAppeared);
        // フェーズ遷移で各分岐グループの判定ウィンドウ起点をリセットする。後半フェーズの分岐が
        // そのフェーズ開始から window_sec 以内に判定できるようにする。
        _phaseSub = bus.Subscribe<PhaseTransitionedEvent>(OnPhaseTransitioned);
    }

    public void Dispose()
    {
        _combatStartSub.Dispose();
        _combatEndSub.Dispose();
        _zoneSub.Dispose();
        _castStartSub.Dispose();
        _statusSub.Dispose();
        _objectSub.Dispose();
        _phaseSub.Dispose();
        Reset();
    }

    /// <summary>
    /// 現在の分岐状態のスナップショット。<see cref="FfxivEchoes.Windows.UpcomingEventsWindow"/>
    /// などが lock 外で安全に参照できる。
    /// </summary>
    public IReadOnlyDictionary<string, BranchStatus> CurrentStatuses
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, BranchStatus>(_statuses, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>
    /// 指定の branch_id が現在 Rejected か（= 表示しないべきか）。
    /// branch_id == null（共通 mechanic）は常に false（表示）。
    /// </summary>
    public bool IsRejected(string? branchId)
    {
        if (string.IsNullOrEmpty(branchId)) return false;
        lock (_gate)
        {
            return _statuses.TryGetValue(branchId, out var s) && s == BranchStatus.Rejected;
        }
    }

    /// <summary>
    /// 指定の branch_id を持つ mechanic を「タイムラインに表示するべきか」判定。
    /// 共通（null）は常に true、active branch は true、pending/rejected は false。
    /// 「未確定中は共通だけ見せる」既定ポリシー。
    /// </summary>
    public bool IsActiveOrCommon(string? branchId)
    {
        if (string.IsNullOrEmpty(branchId)) return true;
        lock (_gate)
        {
            // _statuses にエントリがない（戦闘外 / branches 未定義）= 全 mechanic 表示
            if (_statuses.Count == 0) return true;
            return _statuses.TryGetValue(branchId, out var s) && s == BranchStatus.Active;
        }
    }

    private void Reset()
    {
        BranchStatus[] hadAny;
        lock (_gate)
        {
            hadAny = new BranchStatus[_statuses.Count];
            _statuses.Clear();
            _groupActivatedAt.Clear();
            _combatStartedAt = null;
        }
        if (hadAny.Length > 0)
        {
            _log.Debug("[FfxivEchoes] BranchObserver: 状態クリア zone={Zone}", _currentZone);
        }
    }

    private void OnCombatStarted(CombatStartedEvent ev)
    {
        var file = _store.GetByZone(_currentZone);
        if (file is null || file.Branches.Count == 0)
        {
            // 分岐なしのコンテンツはサービスとして待機状態
            return;
        }
        lock (_gate)
        {
            _statuses.Clear();
            _groupActivatedAt.Clear();
            foreach (var b in file.Branches)
            {
                if (string.IsNullOrEmpty(b.Id)) continue;
                _statuses[b.Id] = BranchStatus.Pending;
                _groupActivatedAt[BranchGroupKey(b)] = ev.Timestamp;
            }
            _combatStartedAt = ev.Timestamp;
        }
        _log.Information("[FfxivEchoes] BranchObserver: {N} branches Pending zone={Zone}",
            file.Branches.Count, _currentZone);
    }

    private void OnPhaseTransitioned(PhaseTransitionedEvent ev)
    {
        // フェーズ遷移時、まだ未確定の分岐グループの判定ウィンドウ起点を現在時刻にリセットする。
        // これにより後半フェーズの分岐が「そのフェーズ開始から window_sec 以内」で判定できる。
        // 既に Active が確定したグループは EvaluateAgainstBranches の Active ガードで再評価されない
        // ため、リセットしても実害はない。
        lock (_gate)
        {
            if (_groupActivatedAt.Count == 0) return;
            var now = ev.Timestamp;
            foreach (var key in new List<string>(_groupActivatedAt.Keys))
            {
                _groupActivatedAt[key] = now;
            }
        }
    }

    private void OnCastStarted(CastStartedEvent ev) =>
        EvaluateAgainstBranches(b => MatchesFirstCast(b.Condition, ev), $"cast 0x{ev.CastActionId:X4}");

    private void OnStatusGained(StatusGainedEvent ev) =>
        EvaluateAgainstBranches(b => MatchesStatusGain(b.Condition, ev), $"status {ev.StatusId}");

    private void OnObjectAppeared(ObjectAppearedEvent ev) =>
        EvaluateAgainstBranches(b => MatchesObjectAppear(b.Condition, ev), $"obj {ev.ObjectName}");

    /// <summary>
    /// 観測イベントが branch のどれかにマッチしたら active / 残りを rejected に遷移。
    /// window_sec 経過後の観測は無視する。
    /// </summary>
    private void EvaluateAgainstBranches(Func<TimelineBranch, bool> isMatch, string evDebug)
    {
        if (_combatStartedAt is null) return;
        var file = _store.GetByZone(_currentZone);
        if (file is null || file.Branches.Count == 0) return;

        TimelineBranch? matched = null;
        var now = DateTimeOffset.UtcNow;
        // 分岐グループごとの起点スナップショット（ロック外で安全に参照するためコピー）。
        Dictionary<string, DateTimeOffset> groupStarts;
        DateTimeOffset combatStart;
        lock (_gate)
        {
            groupStarts = new Dictionary<string, DateTimeOffset>(_groupActivatedAt, StringComparer.Ordinal);
            combatStart = _combatStartedAt.Value;
        }

        // window_sec を超えたイベントは判定しない（グループごとの起点から計測する）
        foreach (var b in file.Branches)
        {
            if (string.IsNullOrEmpty(b.Id)) continue;
            var groupStart = groupStarts.TryGetValue(BranchGroupKey(b), out var ga) ? ga : combatStart;
            if (!IsWithinBranchWindow(now, groupStart, b.Condition.WindowSec)) continue;
            if (isMatch(b))
            {
                matched = b;
                break;
            }
        }
        if (matched is null) return;

        // 既に確定済み（Active / Rejected）なら何もしない。フェーズ遷移で window 起点が
        // リセットされた後、棄却済み分岐の判定キャストが再観測されても再 Active 化しないようにする。
        lock (_gate)
        {
            if (_statuses.TryGetValue(matched.Id, out var s) &&
                s is BranchStatus.Active or BranchStatus.Rejected)
            {
                return;
            }

            var next = ResolveMatchedBranchStatuses(file.Branches, matched.Id, _statuses);
            _statuses.Clear();
            foreach (var (id, status) in next)
            {
                _statuses[id] = status;
            }
        }

        var rejected = new List<string>();
        foreach (var b in file.Branches)
        {
            if (!string.IsNullOrEmpty(b.Id) &&
                b.Id != matched.Id &&
                string.Equals(BranchGroupKey(b), BranchGroupKey(matched), StringComparison.Ordinal))
            {
                rejected.Add(b.Id);
            }
        }

        _log.Information("[FfxivEchoes] BranchObserver: branch '{Active}' active by {Ev} (rejected: {Rejected})",
            matched.Id, evDebug, string.Join(",", rejected));

        _bus.Publish(new BranchResolvedEvent(
            DateTimeOffset.UtcNow,
            _currentZone,
            matched.Id,
            rejected));
    }

    /// <summary>
    /// 観測時刻が分岐グループの判定ウィンドウ内か。<paramref name="groupActivatedAt"/> は
    /// 戦闘開始時刻、またはフェーズ遷移でリセットされた起点。Dalamud 型非依存で単体テスト可能。
    /// </summary>
    public static bool IsWithinBranchWindow(DateTimeOffset now, DateTimeOffset groupActivatedAt, double windowSec)
        => (now - groupActivatedAt).TotalSeconds <= Math.Max(0, windowSec);

    public static IReadOnlyDictionary<string, BranchStatus> ResolveMatchedBranchStatuses(
        IReadOnlyList<TimelineBranch> branches,
        string matchedBranchId,
        IReadOnlyDictionary<string, BranchStatus>? current = null)
    {
        var matched = FindBranch(branches, matchedBranchId);
        var matchedGroup = matched is null ? DefaultBranchGroupId : BranchGroupKey(matched);
        var next = new Dictionary<string, BranchStatus>(StringComparer.Ordinal);

        foreach (var b in branches)
        {
            if (string.IsNullOrEmpty(b.Id)) continue;
            var existing = current is not null && current.TryGetValue(b.Id, out var status)
                ? status
                : BranchStatus.Pending;
            next[b.Id] = string.Equals(BranchGroupKey(b), matchedGroup, StringComparison.Ordinal)
                ? (b.Id == matchedBranchId ? BranchStatus.Active : BranchStatus.Rejected)
                : existing;
        }

        return next;
    }

    private const string DefaultBranchGroupId = "default";

    public static string BranchGroupKey(TimelineBranch branch)
        => string.IsNullOrWhiteSpace(branch.GroupId) ? DefaultBranchGroupId : branch.GroupId!;

    private static TimelineBranch? FindBranch(IReadOnlyList<TimelineBranch> branches, string branchId)
    {
        foreach (var b in branches)
        {
            if (string.Equals(b.Id, branchId, StringComparison.Ordinal))
            {
                return b;
            }
        }

        return null;
    }

    private static bool MatchesFirstCast(BranchCondition cond, CastStartedEvent ev)
    {
        if (cond.Type != "first_cast") return false;
        if (!string.IsNullOrEmpty(cond.CastId) &&
            AoeResolver.TryParseCastId(cond.CastId, out var id) && id != 0)
        {
            return ev.CastActionId == id;
        }
        if (!string.IsNullOrEmpty(cond.CastName))
        {
            return !string.IsNullOrEmpty(ev.CastActionName) &&
                ev.CastActionName.Contains(cond.CastName, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static bool MatchesStatusGain(BranchCondition cond, StatusGainedEvent ev)
    {
        if (cond.Type != "status_gain") return false;
        return cond.StatusId is { } sid && sid != 0 && ev.StatusId == sid;
    }

    private static bool MatchesObjectAppear(BranchCondition cond, ObjectAppearedEvent ev)
    {
        if (cond.Type != "object_appear") return false;
        if (string.IsNullOrEmpty(cond.ObjectName)) return false;
        return !string.IsNullOrEmpty(ev.ObjectName) &&
            ev.ObjectName.Contains(cond.ObjectName, StringComparison.OrdinalIgnoreCase);
    }
}
