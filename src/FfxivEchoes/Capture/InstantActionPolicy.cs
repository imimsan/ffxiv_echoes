using System;

namespace FfxivEchoes.Capture;

public static class InstantActionPolicy
{
    public const float InstantCastThresholdSeconds = 0.05f;
    public const double DuplicateSuppressWindowSeconds = 0.6;

    public static readonly TimeSpan DuplicateSuppressWindow =
        TimeSpan.FromSeconds(DuplicateSuppressWindowSeconds);

    public static bool ShouldPublish(
        uint actionId,
        float totalCast,
        uint lastActionId,
        DateTimeOffset? lastActionAt,
        DateTimeOffset now)
    {
        if (actionId == 0 || totalCast > InstantCastThresholdSeconds)
        {
            return false;
        }

        if (actionId != lastActionId || lastActionAt is null)
        {
            return true;
        }

        return now - lastActionAt.Value >= DuplicateSuppressWindow;
    }
}
