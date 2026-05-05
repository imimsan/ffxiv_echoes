using System;
using System.Numerics;

namespace FfxivEchoes.SafeZone;

public sealed record SafeZoneResult(
    Vector3 WorldPosition,
    DirectionInfo? FromPlayer = null);

public sealed record DirectionInfo(
    float DirectionDeg,
    string DirectionCardinal,
    float DirectionClock,
    float Distance)
{
    public static DirectionInfo Compute(Vector3 from, Vector3 to)
    {
        var dx = to.X - from.X;
        var dz = to.Z - from.Z;
        var distance = MathF.Sqrt(dx * dx + dz * dz);

        // FFXIV：-Z = 北。MathF.Atan2 は標準の (y, x) なので変換に注意
        var angleRad = MathF.Atan2(dx, -dz);
        var angleDeg = NormalizeAngle(angleRad * 180f / MathF.PI);

        return new DirectionInfo(
            DirectionDeg: angleDeg,
            DirectionCardinal: AngleToCardinal(angleDeg),
            DirectionClock: AngleToClock(angleDeg),
            Distance: distance);
    }

    private static float NormalizeAngle(float deg)
    {
        deg %= 360f;
        if (deg < 0)
        {
            deg += 360f;
        }
        return deg;
    }

    private static string AngleToCardinal(float deg)
    {
        // 0=N, 45=NE, 90=E, 135=SE, 180=S, 225=SW, 270=W, 315=NW
        var idx = (int)MathF.Round(deg / 45f) % 8;
        return idx switch
        {
            0 => "N",
            1 => "NE",
            2 => "E",
            3 => "SE",
            4 => "S",
            5 => "SW",
            6 => "W",
            7 => "NW",
            _ => "N",
        };
    }

    private static float AngleToClock(float deg)
    {
        // 0 度 = 12 時、90 度 = 3 時。clock = (deg / 30) % 12、0 → 12
        var clock = deg / 30f;
        if (clock <= 0.001f)
        {
            return 12f;
        }
        return clock;
    }
}
