using System;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace FfxivEchoes.Replay.MockServices;

/// <summary>
/// 時刻駆動の <see cref="IFramework"/> 実装。
/// </summary>
/// <remarks>
/// <para>
/// Replay 時は本物の Framework のフレームループは無い。代わりに <see cref="AdvanceTo"/>
/// で「再生 jsonl 上の現在時刻」を進め、その都度 Update イベントを発火する。
/// AddObjectAoeService / PredictedObjectSpawnService 等の per-frame ループサービスはこれで動く。
/// </para>
/// <para>
/// RunOnFrameworkThread は同期実行する（テストスレッドそのもので OK）。
/// 多くの未使用メンバーは <see cref="NotImplementedException"/> や no-op で塞ぐ。
/// </para>
/// </remarks>
public sealed class MockFramework : IFramework
{
    private DateTime _now = DateTime.UtcNow;
    private long _frame;
    private TimeSpan _lastUpdateDelta = TimeSpan.Zero;

    public event IFramework.OnUpdateDelegate? Update;

    /// <summary>仮想時刻を target まで進めて、各 tick で Update を発火する。</summary>
    /// <remarks>
    /// jsonl の毎行 publish 前に呼ぶ。実用上は「target == 次行の時刻」が呼ばれるので、
    /// その差分が前回 update からの delta になる。
    /// </remarks>
    public void AdvanceTo(DateTimeOffset target)
    {
        var targetUtc = target.UtcDateTime;
        if (targetUtc < _now) return;
        var delta = targetUtc - _now;
        _lastUpdateDelta = delta;
        _now = targetUtc;
        _frame++;
        try
        {
            Update?.Invoke(this);
        }
        catch
        {
            // サービスが Framework.Update で投げても再生は続ける。
        }
    }

    // ── IFramework メンバー ───────────────────────────────────────

    public DateTime LastUpdate => _now;
    public DateTime LastUpdateUTC => _now;
    public TimeSpan UpdateDelta => _lastUpdateDelta;
    public long LastUpdateFrame => _frame;
    public bool IsInFrameworkUpdateThread => true;
    public bool IsFrameworkUnloading => false;

    public TaskFactory GetTaskFactory() => Task.Factory;

    public Task DelayTicks(long numTicks, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    // 設計書 §4.3 オーケストレーション「RunOnFrameworkThread は同期実行」。
    public Task RunOnFrameworkThread(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    public Task<T> RunOnFrameworkThread<T>(Func<T> func) => Task.FromResult(func());

    public Task RunOnFrameworkThread(Func<Task> action) => action();

    public Task<T> RunOnFrameworkThread<T>(Func<Task<T>> action) => action();

    public Task Run(Action action, CancellationToken cancellationToken = default)
    {
        action();
        return Task.CompletedTask;
    }

    public Task<T> Run<T>(Func<T> func, CancellationToken cancellationToken = default)
        => Task.FromResult(func());

    public Task Run(Func<Task> func, CancellationToken cancellationToken = default) => func();

    public Task<T> Run<T>(Func<Task<T>> func, CancellationToken cancellationToken = default) => func();

    public Task RunOnTick(Action action, TimeSpan delay = default, int delayTicks = 0,
        CancellationToken cancellationToken = default)
    {
        action();
        return Task.CompletedTask;
    }

    public Task<T> RunOnTick<T>(Func<T> func, TimeSpan delay = default, int delayTicks = 0,
        CancellationToken cancellationToken = default)
        => Task.FromResult(func());

    public Task RunOnTick(Func<Task> func, TimeSpan delay = default, int delayTicks = 0,
        CancellationToken cancellationToken = default)
        => func();

    public Task<T> RunOnTick<T>(Func<Task<T>> func, TimeSpan delay = default, int delayTicks = 0,
        CancellationToken cancellationToken = default)
        => func();
}
