using System;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;

namespace FfxivEchoes.Triggers;

/// <summary>
/// 戦闘中の「現在フェーズ」を追跡する。<see cref="PhaseTransitionedEvent"/>（sync_point 通過 /
/// hp_pct 跨ぎを検知元とする）を受けて現在フェーズ序数を単調前進させ、過去フェーズのギミックを
/// タイムライン / 読み上げから除外するための判定 <see cref="IsPhaseActive"/> を提供する。
/// </summary>
/// <remarks>
/// フェーズ未注釈（sync_point / hp_pct トリガーに phase が無い）コンテンツでは
/// <see cref="PhaseTransitionPolicy.BuildPhaseOrdinals"/> が空マップを返すため、
/// <see cref="IsPhaseActive"/> は常に true（全表示）になり挙動は変わらない（後方互換）。
/// </remarks>
public sealed class CurrentPhaseTracker : IDisposable
{
    private readonly TriggerStore _store;
    private readonly IPluginLog _log;

    private readonly IDisposable _phaseSub;
    private readonly IDisposable _combatStartSub;
    private readonly IDisposable _combatEndSub;
    private readonly IDisposable _zoneSub;

    private readonly object _gate = new();
    private string _currentZone = "Unknown";
    private int _currentOrdinal;
    private string? _currentPhaseId;
    // フェーズ序数マップのキャッシュ。Schedule から mechanic 件数ぶん IsPhaseActive が
    // 呼ばれるため、毎回 BuildPhaseOrdinals（全 mechanic 走査）するのを避ける。
    private System.Collections.Generic.IReadOnlyDictionary<string, int>? _cachedOrdinals;

    public CurrentPhaseTracker(IEventBus bus, TriggerStore store, IPluginLog log)
    {
        _store = store;
        _log = log;

        _combatStartSub = bus.Subscribe<CombatStartedEvent>(_ => Reset());
        _combatEndSub = bus.Subscribe<CombatEndedEvent>(_ => Reset());
        _zoneSub = bus.Subscribe<ZoneChangedEvent>(z =>
        {
            _currentZone = string.IsNullOrEmpty(z.ZoneName) ? "Unknown" : z.ZoneName;
            Reset();
        });
        _phaseSub = bus.Subscribe<PhaseTransitionedEvent>(OnPhaseTransitioned);
    }

    public void Dispose()
    {
        _phaseSub.Dispose();
        _combatStartSub.Dispose();
        _combatEndSub.Dispose();
        _zoneSub.Dispose();
    }

    /// <summary>現在フェーズ序数（0 = 初期フェーズ）。</summary>
    public int CurrentPhaseOrdinal
    {
        get { lock (_gate) return _currentOrdinal; }
    }

    /// <summary>直近に入場したフェーズ ID（未遷移なら null）。</summary>
    public string? CurrentPhaseId
    {
        get { lock (_gate) return _currentPhaseId; }
    }

    private void Reset()
    {
        lock (_gate)
        {
            _currentOrdinal = 0;
            _currentPhaseId = null;
            _cachedOrdinals = null;
        }
    }

    private void OnPhaseTransitioned(PhaseTransitionedEvent ev)
    {
        var file = _store.GetByZone(_currentZone);
        if (file is null)
        {
            return;
        }

        var ordinals = PhaseTransitionPolicy.BuildPhaseOrdinals(file);
        var ord = PhaseTransitionPolicy.ResolvePhaseOrdinal(ev.PhaseId, ordinals);

        lock (_gate)
        {
            _cachedOrdinals = ordinals;
            // 単調前進のみ。後退・同一序数（重複通知 / 戻り判定）は無視する。
            if (ord <= _currentOrdinal)
            {
                return;
            }
            _currentOrdinal = ord;
            _currentPhaseId = ev.PhaseId;
        }

        _log.Information("[FfxivEchoes] フェーズ遷移: {Phase} (ordinal {Ord}) zone={Zone}",
            ev.PhaseId, ord, _currentZone);
    }

    /// <summary>
    /// mechanic.Phase が「現在以降のフェーズ」なら true（表示・通知してよい）、
    /// 既に過ぎた過去フェーズなら false（抑制すべき）。
    /// フェーズ未注釈のコンテンツでは常に true。
    /// </summary>
    public bool IsPhaseActive(string? mechanicPhase)
    {
        int current;
        System.Collections.Generic.IReadOnlyDictionary<string, int>? ordinals;
        lock (_gate)
        {
            current = _currentOrdinal;
            ordinals = _cachedOrdinals;
        }

        if (ordinals is null)
        {
            // 初回（未遷移 = 初期フェーズ）。1 度だけ構築してキャッシュする。
            var file = _store.GetByZone(_currentZone);
            if (file is null)
            {
                return true;
            }
            ordinals = PhaseTransitionPolicy.BuildPhaseOrdinals(file);
            lock (_gate)
            {
                _cachedOrdinals = ordinals;
            }
        }

        return !PhaseTransitionPolicy.IsPastPhase(mechanicPhase, current, ordinals);
    }
}
