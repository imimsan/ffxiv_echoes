using System;
using FfxivEchoes.Events;

namespace FfxivEchoes.Capture;

/// <summary>
/// 戦闘開始からの相対時刻を提供する。CombatStartedEvent / CombatEndedEvent を購読して状態を維持する。
/// </summary>
public sealed class CombatClock : IDisposable
{
    private readonly IDisposable _startedSub;
    private readonly IDisposable _endedSub;
    private DateTimeOffset? _startedAt;

    public CombatClock(IEventBus bus)
    {
        _startedSub = bus.Subscribe<CombatStartedEvent>(OnStart);
        _endedSub = bus.Subscribe<CombatEndedEvent>(OnEnd);
    }

    public bool InCombat => _startedAt.HasValue;

    public DateTimeOffset? CombatStartedAt => _startedAt;

    /// <summary>
    /// 戦闘開始からの相対秒。戦闘外なら null。
    /// </summary>
    public double? RelativeSecondsAt(DateTimeOffset timestamp)
    {
        if (_startedAt is not { } start)
        {
            return null;
        }
        return (timestamp - start).TotalSeconds;
    }

    private void OnStart(CombatStartedEvent ev) => _startedAt = ev.Timestamp;
    private void OnEnd(CombatEndedEvent _) => _startedAt = null;

    public void Dispose()
    {
        _startedSub.Dispose();
        _endedSub.Dispose();
    }
}
