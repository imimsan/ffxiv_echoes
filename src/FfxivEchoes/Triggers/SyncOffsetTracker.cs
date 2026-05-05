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
    private readonly TriggerStore _store;
    private readonly CombatClock _combatClock;
    private readonly RecordingScanner _recordings;
    private readonly IPluginLog _log;

    private readonly IDisposable _castSub;
    private readonly IDisposable _zoneSub;
    private readonly IDisposable _combatStartSub;

    private string _currentZone = "Unknown";
    private AggregatedEvents? _aggCache;
    private readonly object _gate = new();
    private double _offsetSec;
    private DateTimeOffset _lastUpdatedAt;
    private string? _lastTriggerLabel;

    private const double MaxAcceptableJumpSec = 30.0;

    public SyncOffsetTracker(IEventBus bus, TriggerStore store, CombatClock clock,
        RecordingScanner recordings, IPluginLog log)
    {
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
    }

    public void Dispose()
    {
        _castSub.Dispose();
        _zoneSub.Dispose();
        _combatStartSub.Dispose();
    }

    /// <summary>現在の同期オフセット（秒）。録画の予測時刻に加えて使う。</summary>
    public double CurrentOffsetSec
    {
        get { lock (_gate) return _offsetSec; }
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
            _lastUpdatedAt = default;
            _lastTriggerLabel = null;
        }
    }

    private void ReloadAggregate()
    {
        try { _aggCache = _recordings.Aggregate(_currentZone); } catch { _aggCache = null; }
    }

    private void OnCastStart(CastStartedEvent ev)
    {
        var nowRel = _combatClock.RelativeSecondsAt(ev.Timestamp);
        if (nowRel is null) return; // 戦闘外

        var file = _store.GetByZone(_currentZone);
        if (file is null) return;

        var actualRel = nowRel.Value;
        // 1. SyncPoint で明示されている cast_id にマッチするか
        foreach (var sp in file.SyncPoints)
        {
            if (sp.Type != "cast_start") continue;
            if (string.IsNullOrEmpty(sp.CastId)) continue;
            if (!CastIdEquals(sp.CastId, ev.CastActionId)) continue;
            ApplyOffset(actualRel - sp.ExpectedTime, $"sync:{sp.Id}");
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
            var expected = aggEv.FirstSeenSeconds;
            ApplyOffset(actualRel - expected, $"rec:{ev.CastActionName}");
            return;
        }
    }

    private void ApplyOffset(double newOffset, string label)
    {
        // 急激なジャンプは誤検知扱いで無視（最初の同期で大きい値はそのまま採用）
        lock (_gate)
        {
            if (_lastUpdatedAt != default && Math.Abs(newOffset - _offsetSec) > MaxAcceptableJumpSec)
            {
                _log.Debug("[FfxivEchoes] Sync offset 拒否（jump 過大）：current={Cur:0.00} new={New:0.00}",
                    _offsetSec, newOffset);
                return;
            }
            var diff = newOffset - _offsetSec;
            _offsetSec = newOffset;
            _lastUpdatedAt = DateTimeOffset.UtcNow;
            _lastTriggerLabel = label;
            _log.Information("[FfxivEchoes] Sync offset 更新: {Off:+0.0;-0.0;0}s (Δ {Diff:+0.0;-0.0;0}s) via {Label}",
                _offsetSec, diff, label);
        }
    }

    /// <summary>"0x189E" 形式の文字列と uint を比較（"0x" / "#" prefix 許容）。</summary>
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
