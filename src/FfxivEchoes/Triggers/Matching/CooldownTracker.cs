using System;
using System.Collections.Generic;

namespace FfxivEchoes.Triggers.Matching;

/// <summary>
/// トリガーの連続発火を防ぐクールダウン管理。
/// </summary>
public sealed class CooldownTracker
{
    private readonly Dictionary<string, DateTimeOffset> _lastFiredAt = new(StringComparer.OrdinalIgnoreCase);

    public bool IsOnCooldown(string triggerId, double cooldownSeconds, DateTimeOffset now)
    {
        if (cooldownSeconds <= 0)
        {
            return false;
        }
        if (!_lastFiredAt.TryGetValue(triggerId, out var last))
        {
            return false;
        }
        return (now - last).TotalSeconds < cooldownSeconds;
    }

    public void MarkFired(string triggerId, DateTimeOffset firedAt)
    {
        _lastFiredAt[triggerId] = firedAt;
    }

    public void Reset()
    {
        _lastFiredAt.Clear();
    }
}
