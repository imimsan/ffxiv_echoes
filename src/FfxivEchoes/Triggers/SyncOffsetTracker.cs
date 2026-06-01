using System;
using Dalamud.Plugin.Services;
using FfxivEchoes.Capture;
using FfxivEchoes.Events;
using FfxivEchoes.Recording;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Triggers;

/// <summary>
/// 録画ベースの予測タイムラインに対する「実戦の時刻ズレ」を追跡する。
/// 同じキャストが録画では t=30s で起きたが、今回は t=38s で起きた → offset = +8s。
/// 以降のすべての予測 / ノートに +8s 加えて表示・通知すれば追従できる。
/// </summary>
/// <remarks>
/// SyncPoint（auto_settings 経由でゾーン定義に書く想定）に加え、録画 aggregate に
/// 出現したキャストもすべて参考点として使う。最新の観測ほど信頼度が高いので
/// 単純な「最新値で上書き」方式（EMA でない）。誤検知を避けるため、
/// 観測時刻と予測時刻の差が大きすぎる場合（>30 秒）は無視する。
/// </remarks>
public sealed class SyncOffsetTracker : IDisposable
{
    private readonly IEventBus _bus;
    private readonly TriggerStore _store;
    private readonly CombatClock _combatClock;
    private readonly RecordingScanner _recordings;
    private readonly IPluginLog _log;

    private readonly IDisposable _castSub;
    private readonly IDisposable _zoneSub;
    private readonly IDisposable _combatStartSub;
    private readonly IDisposable _phaseSub;

    private string _currentZone = "Unknown";
    private AggregatedEvents? _aggCache;
    private readonly object _gate = new();
    private double _offsetSec;
    // 発生源（ボス）ごとのオフセット。2 体フェーズで各ボスが独立にドリフトしても、タイムライン表示が
    // 単一グローバル値の上書きで振動しないよう、ボス別に保持する。グローバル _offsetSec は従来どおり
    // 維持し（読み上げタイミング等の既存消費者は不変）、これは表示バー用の純加算。
    private readonly Dictionary<string, double> _offsetBySource = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastUpdatedAt;
    private string? _lastTriggerLabel;
    // フェーズ遷移直後の短時間だけ、録画 aggregate 経路の大ジャンプ再アンカーを許可する武装時刻。
    // フェーズ境界ではオフセット基準が正当に大きく変わりうるため、その直後の再アンカーをガードで弾かない。
    // ただし「遷移後しばらく経ってから来た無関係なキャスト」に大ジャンプを消費されないよう時刻窓で限定する。
    private DateTimeOffset? _largeJumpArmedAt;

    // フェーズ遷移から大ジャンプ再アンカーを許可する猶予（秒）。
    private const double LargeJumpWindowSec = 2.0;

    private const double MaxAcceptableJumpSec = 30.0;

    public SyncOffsetTracker(IEventBus bus, TriggerStore store, CombatClock clock,
        RecordingScanner recordings, IPluginLog log)
    {
        _bus = bus;
        _store = store;
        _combatClock = clock;
        _recordings = recordings;
        _log = log;

        _castSub = bus.Subscribe<CastStartedEvent>(OnCastStart);
        _zoneSub = bus.Subscribe<ZoneChangedEvent>(z =>
        {
            _currentZone = string.IsNullOrEmpty(z.ZoneName) ? "Unknown" : z.ZoneName;
            ResetState();
            ReloadAggregate();
        });
        _combatStartSub = bus.Subscribe<CombatStartedEvent>(_ =>
        {
            ResetState();
            ReloadAggregate();
        });
        // フェーズ遷移を購読：直後の短い時刻窓だけ録画 aggregate 経路の大ジャンプ再アンカーを許可。
        _phaseSub = bus.Subscribe<PhaseTransitionedEvent>(_ =>
        {
            lock (_gate)
            {
                _largeJumpArmedAt = DateTimeOffset.UtcNow;
            }
        });
    }

    public void Dispose()
    {
        _castSub.Dispose();
        _zoneSub.Dispose();
        _combatStartSub.Dispose();
        _phaseSub.Dispose();
    }

    /// <summary>現在の同期オフセット（秒）。録画の予測時刻に加えて使う。</summary>
    public double CurrentOffsetSec
    {
        get { lock (_gate) return _offsetSec; }
    }

    /// <summary>
    /// 指定発生源（ボス）の同期オフセット（秒）。タイムライン表示が 2 体フェーズで一方のボスの
    /// ドリフトに引きずられないよう、ボス別の値を返す。未記録の source（1 体運用や source 不一致）は
    /// グローバル <see cref="CurrentOffsetSec"/> にフォールバックするため、従来挙動を退行させない。
    /// </summary>
    public double OffsetForSource(string? source)
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(source) && _offsetBySource.TryGetValue(source!, out var v))
            {
                return v;
            }
            return _offsetSec;
        }
    }

    /// <summary>UI 表示用：直近の同期トリガー名と最終更新時刻。</summary>
    public (string? Label, DateTimeOffset At) LastSyncInfo
    {
        get { lock (_gate) return (_lastTriggerLabel, _lastUpdatedAt); }
    }

    private void ResetState()
    {
        lock (_gate)
        {
            _offsetSec = 0;
            _offsetBySource.Clear();
            _lastUpdatedAt = default;
            _lastTriggerLabel = null;
            _largeJumpArmedAt = null;
        }
    }

    private void ReloadAggregate()
    {
        try
        {
            _aggCache = _recordings.Aggregate(_currentZone);
        }
        catch (Exception ex)
        {
            // 録画ディレクトリのロック/破損時。黙殺すると同期オフセットが効かず原因不明になるためログを残す。
            _log.Warning(ex, "[FfxivEchoes] SyncOffsetTracker: 録画 aggregate 失敗 zone={Zone}", _currentZone);
            _aggCache = null;
        }
    }

    private void OnCastStart(CastStartedEvent ev)
    {
        var nowRel = _combatClock.RelativeSecondsAt(ev.Timestamp);
        if (nowRel is null) return; // 戦闘外

        var file = _store.GetByZone(_currentZone);
        if (file is null) return;

        var actualRel = nowRel.Value;
        // 1. SyncPoint で明示されている cast_id にマッチするか
        if (TryFindBestSyncPointOffset(
                file.SyncPoints,
                actualRel,
                CurrentOffsetSec,
                ev.CastActionId,
                out var syncOffset,
                out var syncLabel,
                out var syncPhase))
        {
            // sync_point 経路はマッチ時点で distance ≤ tolerance（作者が明示した信頼境界）が保証され、
            // ジャンプ幅 = distance も自動的に tolerance 以下。よって別個の 30 秒ガードは冗長かつ有害で、
            // tolerance>30s の sync_point（フェーズ境界の大きな再アンカー）を誤って棄却していた。
            // sync_point 一致は常に大ジャンプ許可（録画 aggregate 経路のガードは現状維持）。
            ApplyOffset(syncOffset, syncLabel, allowLargeJump: true, source: ev.SourceName);
            // フェーズ境界の sync_point ならフェーズ遷移を通知（CurrentPhaseTracker が前進）。
            if (!string.IsNullOrEmpty(syncPhase))
            {
                _bus.Publish(new PhaseTransitionedEvent(DateTimeOffset.UtcNow, syncPhase!));
            }
            return;
        }

        // 2. 録画 aggregate に同じ cast がある → expected = FirstSeenSeconds
        var agg = _aggCache;
        if (agg is null) return;
        foreach (var aggEv in agg.Events)
        {
            if (aggEv.Key.Type != "cast_start") continue;
            if (string.IsNullOrEmpty(aggEv.Key.Id)) continue;
            if (!CastIdEquals(aggEv.Key.Id, ev.CastActionId)) continue;
            // 同じキャストでも複数回出るので、最も近い予測値を使う
            var expected = RecordingPredictionPlanner.FindClosestObservedTime(
                aggEv,
                actualRel,
                CurrentOffsetSec);
            // 録画経路は通常ジャンプガードを効かせる（フェーズ跨ぎで同一 cast_id が再利用された際に
            // 前半フェーズの観測時刻を拾って大きな誤オフセットへ飛ぶのを防ぐ）。
            // フェーズ遷移直後の時刻窓内（LargeJumpWindowSec）に来たキャストだけ正当な再アンカーとして
            // 大ジャンプを許可する。窓外/未武装なら通常ガード。武装は一度の判定で失効させる。
            bool allowJump;
            lock (_gate)
            {
                allowJump = _largeJumpArmedAt is { } armed &&
                            (DateTimeOffset.UtcNow - armed).TotalSeconds <= LargeJumpWindowSec;
                _largeJumpArmedAt = null;
            }
            ApplyOffset(actualRel - expected, $"rec:{ev.CastActionName}", allowLargeJump: allowJump, source: ev.SourceName);
            return;
        }
    }

    private void ApplyOffset(double newOffset, string label, bool allowLargeJump, string? source = null)
    {
        // 急激なジャンプは誤検知扱いで無視（最初の同期で大きい値はそのまま採用）
        lock (_gate)
        {
            if (!ShouldAcceptOffsetJump(_lastUpdatedAt != default, _offsetSec, newOffset, allowLargeJump))
            {
                _log.Debug("[FfxivEchoes] Sync offset 拒否（jump 過大）：current={Cur:0.00} new={New:0.00}",
                    _offsetSec, newOffset);
                return;
            }
            var diff = newOffset - _offsetSec;
            _offsetSec = newOffset;
            // 発生源（ボス）別オフセットも更新（2 体フェーズで表示バーがボス別に追従できるように）。
            if (!string.IsNullOrEmpty(source))
            {
                _offsetBySource[source!] = newOffset;
            }
            _lastUpdatedAt = DateTimeOffset.UtcNow;
            _lastTriggerLabel = label;
            _log.Information("[FfxivEchoes] Sync offset 更新: {Off:+0.0;-0.0;0}s (Δ {Diff:+0.0;-0.0;0}s) via {Label}",
                _offsetSec, diff, label);
        }
    }

    public static bool ShouldAcceptOffsetJump(
        bool hasPreviousSync,
        double currentOffsetSec,
        double newOffsetSec,
        bool allowLargeJump)
    {
        return allowLargeJump ||
               !hasPreviousSync ||
               Math.Abs(newOffsetSec - currentOffsetSec) <= MaxAcceptableJumpSec;
    }

    /// <summary>"0x189E" 形式の文字列と uint を比較（"0x" / "#" prefix 許容）。</summary>
    public static bool TryFindBestSyncPointOffset(
        IEnumerable<SyncPoint> syncPoints,
        double actualRelSec,
        double currentOffsetSec,
        uint actualCastId,
        out double newOffsetSec,
        out string label,
        out string? phase)
    {
        SyncPoint? best = null;
        var bestDistance = double.MaxValue;

        foreach (var sp in syncPoints)
        {
            if (sp.Type != "cast_start") continue;
            if (string.IsNullOrEmpty(sp.CastId)) continue;
            if (!CastIdEquals(sp.CastId, actualCastId)) continue;

            var predictedActual = sp.ExpectedTime + currentOffsetSec;
            var distance = Math.Abs(actualRelSec - predictedActual);
            if (distance > Math.Max(0, sp.Tolerance))
            {
                continue;
            }

            if (distance < bestDistance)
            {
                best = sp;
                bestDistance = distance;
            }
        }

        if (best is null)
        {
            newOffsetSec = 0;
            label = string.Empty;
            phase = null;
            return false;
        }

        newOffsetSec = actualRelSec - best.ExpectedTime;
        label = $"sync:{best.Id}";
        phase = best.Phase;
        return true;
    }

    private static bool CastIdEquals(string spec, uint actualId)
    {
        if (string.IsNullOrEmpty(spec)) return false;
        var s = spec;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (s.StartsWith("#")) s = s[1..];
        return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed == actualId;
    }
}
