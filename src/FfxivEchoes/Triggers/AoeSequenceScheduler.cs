using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Triggers;

/// <summary>
/// 時間差 / 連鎖 AoE のスケジューラ。<see cref="AoeSequence"/> を受け取って
/// 各 <see cref="AoeSequenceStep"/> を <see cref="AoeSequenceStep.DelaySec"/> 後に
/// コールバック経由で発火する。
/// </summary>
/// <remarks>
/// <para>
/// FFXIV プラグインのスレッド安全規則：ObjectTable はメインスレッド（Framework.Update 経由）
/// 限定。<see cref="System.Threading.Tasks.Task.Delay"/> は使わず、
/// <see cref="IFramework.Update"/> で <see cref="DateTimeOffset.UtcNow"/> ベースに消化する。
/// </para>
/// <para>
/// キャスト cancel での挙動：<see cref="Cancel"/> を呼ぶと <c>mechanicKey</c> に紐付いた
/// 未発火ステップが除去される。すでに発火済みの AoE は呼び出し側（ActorTrackedAoeService）の
/// ExpiresAt で自然消滅させる（Splatoon 流のステートレス再評価）。
/// </para>
/// </remarks>
public sealed class AoeSequenceScheduler : IDisposable
{
    private readonly IFramework _framework;
    private readonly Action<AoeSequenceStep, IGameEvent, uint, string> _onFire;
    private readonly IPluginLog _log;
    private readonly List<PendingStep> _pending = new();
    private readonly object _gate = new();
    private volatile bool _disposed;

    /// <param name="onFire">
    /// (step, sourceEvent, sourceActorId, mechanicKey) を引数に受ける発火コールバック。
    /// 通常は <see cref="ActorTrackedAoeService"/> の Track API を呼ぶ。
    /// </param>
    public AoeSequenceScheduler(
        IFramework framework,
        Action<AoeSequenceStep, IGameEvent, uint, string> onFire,
        IPluginLog log)
    {
        _framework = framework;
        _onFire = onFire;
        _log = log;
        _framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        _disposed = true;
        try { _framework.Update -= OnUpdate; } catch { /* runtime teardown */ }
        lock (_gate)
        {
            _pending.Clear();
        }
    }

    /// <summary>
    /// シーケンスの全ステップを <paramref name="fireBaseAt"/> 起点で予約。
    /// </summary>
    /// <param name="mechanicKey">cancel 時に未発火分を絞り込むキー。
    /// 通常は <c>$"{profileId}/{mechanicId}"</c> 等を使う。</param>
    public void Schedule(
        AoeSequence sequence,
        IGameEvent sourceEvent,
        uint sourceActorId,
        string mechanicKey,
        DateTimeOffset fireBaseAt)
    {
        if (_disposed || sequence?.Steps is null || sequence.Steps.Count == 0) return;

        lock (_gate)
        {
            foreach (var step in sequence.Steps)
            {
                if (step is null || step.Zones is null || step.Zones.Count == 0) continue;
                _pending.Add(new PendingStep(
                    MechanicKey: mechanicKey,
                    FireAt: fireBaseAt.AddSeconds(Math.Max(0, step.DelaySec)),
                    Step: step,
                    Source: sourceEvent,
                    SourceActorId: sourceActorId));
            }
        }
        _log.Debug("[FfxivEchoes] AoeSequenceScheduler: {N} steps scheduled key={Key}",
            sequence.Steps.Count, mechanicKey);
    }

    /// <summary>
    /// 特定 mechanic の未発火ステップを全て取り消す（キャスト cancel 等）。
    /// </summary>
    public void Cancel(string mechanicKey)
    {
        lock (_gate)
        {
            var removed = _pending.RemoveAll(p => p.MechanicKey == mechanicKey);
            if (removed > 0)
            {
                _log.Debug("[FfxivEchoes] AoeSequenceScheduler: canceled {N} pending steps key={Key}",
                    removed, mechanicKey);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
        }
    }

    /// <summary>
    /// テスト・診断用：保留中ステップ数。
    /// </summary>
    public int PendingCount
    {
        get
        {
            lock (_gate) { return _pending.Count; }
        }
    }

    private void OnUpdate(IFramework _)
    {
        if (_disposed) return;
        var now = DateTimeOffset.UtcNow;

        PendingStep[] toFire;
        lock (_gate)
        {
            // _pending を時刻でフィルタして「発火対象」を取り出し、本体から除去
            toFire = _pending.Where(p => p.FireAt <= now).ToArray();
            if (toFire.Length > 0)
            {
                _pending.RemoveAll(p => p.FireAt <= now);
            }
        }
        if (toFire.Length == 0) return;

        // コールバック呼び出しは lock 外（外側で _active を触る ActorTrackedAoeService の
        // ロックと重畳して deadlock しないように）
        foreach (var p in toFire)
        {
            try
            {
                _onFire(p.Step, p.Source, p.SourceActorId, p.MechanicKey);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[FfxivEchoes] AoeSequenceScheduler: step fire callback failed key={Key}",
                    p.MechanicKey);
            }
        }
    }

    private sealed record PendingStep(
        string MechanicKey,
        DateTimeOffset FireAt,
        AoeSequenceStep Step,
        IGameEvent Source,
        uint SourceActorId);
}
