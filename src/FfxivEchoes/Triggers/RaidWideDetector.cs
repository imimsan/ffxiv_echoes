using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Triggers;

/// <summary>
/// 「キャスト発動 → 直後に PT 全員 (or ほぼ全員) が被弾」というパターンを検出して、
/// 該当キャストを <see cref="SafeCallDictionary"/> に raid-wide としてマークする。
/// </summary>
/// <remarks>
/// raid-wide とマークされたキャストは AutoTelegraphService / MinimapWindow が
/// ミニマップに範囲を描画しなくなる（回避不能なので形を見ても意味がないため）。
/// 検出は「キャスト終了 ±0.7 秒以内」に PT メンバー何人が HP 減少 (HpChangedEvent)
/// したかで行い、6/8 以上であれば raid-wide 確定とする。
/// </remarks>
public sealed class RaidWideDetector : IDisposable
{
    /// <summary>キャスト発動から HP 減少を観測する時間窓（秒）。</summary>
    private const double WindowSec = 0.9;

    /// <summary>
    /// raid-wide と判定する最小 PT 被弾人数。8 人 PT で 5/8 = 過半数。
    /// 1 人や 2 人の食いっぱぐれを許容するため、6/8 ではなく 5/8 を採用。
    /// </summary>
    private const int MinHitCount = 5;

    /// <summary>HP 減少と認める下落率（パーセント）。</summary>
    private const float MinHpDropPct = 1.0f;

    private readonly IPartyList _partyList;
    private readonly IPlayerState _playerState;
    private readonly TriggerStore _triggerStore;
    private readonly IPluginLog _log;

    private readonly IDisposable _castSub;
    private readonly IDisposable _castCompleteSub;
    private readonly IDisposable _hpSub;
    private readonly IDisposable _combatEndSub;
    private readonly IDisposable _zoneSub;

    private readonly object _gate = new();

    /// <summary>
    /// 監視中のキャスト：cast_id → (発動した時刻, source actor 名, cast 名, 観測した PT 被弾)
    /// </summary>
    private readonly List<PendingCast> _pending = new();

    /// <summary>各 PT メンバーの最後に観測した HP%（被弾検知用）。</summary>
    private readonly Dictionary<uint, float> _lastHpPct = new();
    private string _currentZone = "Unknown";

    public RaidWideDetector(
        IEventBus bus,
        IPartyList partyList,
        IPlayerState playerState,
        TriggerStore triggerStore,
        IPluginLog log)
    {
        _partyList = partyList;
        _playerState = playerState;
        _triggerStore = triggerStore;
        _log = log;

        _castSub = bus.Subscribe<CastStartedEvent>(OnCastStart);
        _castCompleteSub = bus.Subscribe<CastCompletedEvent>(OnCastComplete);
        _hpSub = bus.Subscribe<HpChangedEvent>(OnHpChanged);
        _combatEndSub = bus.Subscribe<CombatEndedEvent>(_ => Reset());
        _zoneSub = bus.Subscribe<ZoneChangedEvent>(z =>
            _currentZone = string.IsNullOrEmpty(z.ZoneName) ? "Unknown" : z.ZoneName);
    }

    public void Dispose()
    {
        _castSub.Dispose();
        _castCompleteSub.Dispose();
        _hpSub.Dispose();
        _combatEndSub.Dispose();
        _zoneSub.Dispose();
        Reset();
    }

    private void Reset()
    {
        lock (_gate)
        {
            _pending.Clear();
            _lastHpPct.Clear();
        }
    }

    private void OnCastStart(CastStartedEvent ev)
    {
        // PT メンバー or 自分のキャストは raid-wide ではないので無視
        if (IsPartyMemberId(ev.SourceId)) return;
        // キャスト終了予測時刻 = 発動時刻 + CastTime
        var fireAt = ev.Timestamp.AddSeconds(ev.CastTime);
        lock (_gate)
        {
            CleanupExpired(ev.Timestamp);
            _pending.Add(new PendingCast(
                castId: ev.CastActionId,
                castName: ev.CastActionName,
                fireAt: fireAt,
                completedAt: null,
                hitMembers: new HashSet<uint>()));
        }
    }

    private void OnCastComplete(CastCompletedEvent ev)
    {
        lock (_gate)
        {
            CleanupExpired(ev.Timestamp);
            // 該当キャストがあれば「完了時刻」を埋める。以降 WindowSec 内の HP 減少を集計する
            for (var i = _pending.Count - 1; i >= 0; i--)
            {
                if (_pending[i].CastId == ev.CastActionId && _pending[i].CompletedAt is null)
                {
                    _pending[i].CompletedAt = ev.Timestamp;
                    break;
                }
            }
        }
    }

    private void OnHpChanged(HpChangedEvent ev)
    {
        if (!IsPartyMemberId(ev.ActorId))
        {
            return;
        }

        float prev;
        lock (_gate)
        {
            prev = _lastHpPct.TryGetValue(ev.ActorId, out var p) ? p : float.NaN;
            _lastHpPct[ev.ActorId] = ev.HpPct;
        }
        if (float.IsNaN(prev)) return;
        var drop = prev - ev.HpPct;
        if (drop < MinHpDropPct) return; // 回復や微小ジッタは無視

        // どのキャストの完了から WindowSec 以内かを判定
        List<PendingCast>? readyToFinalize = null;
        lock (_gate)
        {
            for (var i = _pending.Count - 1; i >= 0; i--)
            {
                var p = _pending[i];
                var anchor = p.CompletedAt ?? p.FireAt;
                var dt = (ev.Timestamp - anchor).TotalSeconds;
                if (dt < -0.2 || dt > WindowSec)
                {
                    continue;
                }
                p.HitMembers.Add(ev.ActorId);
                if (p.HitMembers.Count >= MinHitCount && !p.Marked)
                {
                    p.Marked = true;
                    readyToFinalize ??= new List<PendingCast>();
                    readyToFinalize.Add(p);
                }
            }
        }

        if (readyToFinalize is null) return;
        foreach (var cast in readyToFinalize)
        {
            try
            {
                MarkRaidWide(cast);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[FfxivEchoes] RaidWide マーク失敗 castId={Id}", cast.CastId);
            }
        }
    }

    private void MarkRaidWide(PendingCast cast)
    {
        var key = $"0x{cast.CastId:X4}";
        var file = _triggerStore.GetByZone(_currentZone);
        if (file is null)
        {
            _log.Debug(
                "[FfxivEchoes] RaidWide detected but no trigger file for zone={Zone}: {Name} ({Key})",
                _currentZone, cast.CastName, key);
            return;
        }

        var hit = AutoSafeCallPlanner.FindRaidWideMarker(file, cast.CastId, cast.CastName);
        if (hit is not null)
        {
            return; // 既にマーク済み
        }

        file.RaidWideMarkers.Add(new RaidWideMarker
        {
            Id = key,
            Name = cast.CastName,
            Callout = $"全体: {cast.CastName}",
            Tts = cast.CastName,
            Source = "hp_correlation",
            Confidence = (double)cast.HitMembers.Count / 8,
        });
        _triggerStore.SaveZone(file.Zone, file);

        _log.Information(
            "[FfxivEchoes] RaidWide detected: {Zone} / {Name} ({Key}) hit {N} party members → コンテンツ別ミニマップ非表示登録",
            file.Zone, cast.CastName, key, cast.HitMembers.Count);
    }

    private bool IsPartyMemberId(uint id)
    {
        if (_playerState.IsLoaded && _playerState.EntityId == id) return true;
        foreach (var m in _partyList)
        {
            if ((uint)m.EntityId == id) return true;
        }
        return false;
    }

    private void CleanupExpired(DateTimeOffset now)
    {
        // 完了から WindowSec + 1.0 秒経ったら忘れる
        _pending.RemoveAll(p =>
        {
            var anchor = p.CompletedAt ?? p.FireAt;
            return (now - anchor).TotalSeconds > WindowSec + 1.0;
        });
    }

    /// <summary>
    /// raid-wide 検出で集計中のキャスト 1 件分の状態。
    /// <para>
    /// あえて class（record ではない）。理由：HitMembers / Marked / CompletedAt が runtime で
    /// 変更される。record + with パターンを混在させると「with でコピーした側の HitMembers が
    /// 元と同じ HashSet を参照する」という値型錯覚バグを誘発するため、可変オブジェクトとして
    /// 明示的に class にする。CastId / CastName / FireAt はコンストラクト後不変なので init/readonly。
    /// </para>
    /// </summary>
    private sealed class PendingCast
    {
        public uint CastId { get; }
        public string CastName { get; }
        public DateTimeOffset FireAt { get; }
        public DateTimeOffset? CompletedAt { get; set; }
        public HashSet<uint> HitMembers { get; }
        public bool Marked { get; set; }

        public PendingCast(uint castId, string castName, DateTimeOffset fireAt,
            DateTimeOffset? completedAt, HashSet<uint> hitMembers)
        {
            CastId = castId;
            CastName = castName;
            FireAt = fireAt;
            CompletedAt = completedAt;
            HitMembers = hitMembers;
        }
    }
}
