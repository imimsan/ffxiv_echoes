using System;
using System.Numerics;

namespace FfxivEchoes.Triggers;

/// <summary>
/// 自動生成 AoE の幾何デフォルト。ミニマップと床塗りで同じ値を使う。
/// </summary>
public static class AoeGeometryPolicy
{
    public const float DefaultLineHalfWidthM = 5.0f;

    public static float ResolveLineHalfWidth(double? requested)
    {
        return requested is > 0 ? (float)requested.Value : DefaultLineHalfWidthM;
    }

    /// <summary>
    /// アリーナ中心からの相対座標にいるオブジェクトが、中心方向へ撃つ角度。
    /// 0=東、90=南、180=西、-90=北。
    /// </summary>
    public static float RotationDegTowardsArenaCenter(float relativeX, float relativeZ)
    {
        if (MathF.Abs(relativeX) < 0.001f && MathF.Abs(relativeZ) < 0.001f)
        {
            return 0f;
        }

        var rad = MathF.Atan2(-relativeZ, -relativeX);
        var deg = rad * 180f / MathF.PI;
        return deg <= -180f + 0.001f ? 180f : deg;
    }

    /// <summary>
    /// FFXIV rotation 系（0=南、π/2=東）で、objectWorld から arenaCenter へ向く角度。
    /// </summary>
    public static float FfxivRotationTowardsArenaCenter(Vector3 objectWorld, Vector3 arenaCenter)
    {
        var dx = arenaCenter.X - objectWorld.X;
        var dz = arenaCenter.Z - objectWorld.Z;
        if (MathF.Abs(dx) < 0.001f && MathF.Abs(dz) < 0.001f)
        {
            return 0f;
        }

        return ActorTrackedAoeService.ComputeTowardsTargetRotation(dx, dz);
    }
}
