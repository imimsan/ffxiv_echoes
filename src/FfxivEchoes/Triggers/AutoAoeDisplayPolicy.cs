using FfxivEchoes.Triggers.Models;
using System;
using System.Numerics;

namespace FfxivEchoes.Triggers;

public readonly record struct AutoAoeArenaConfig(
    double ArenaRadius,
    string ArenaShape,
    double? ArenaWidth,
    double? ArenaDepth,
    Vector3? LockedArenaCenter);

public static class AutoAoeDisplayPolicy
{
    public static bool IsEnabled(TriggerFile? file)
    {
        return file?.AutoSettings.ShowAutoTelegraphs == true;
    }

    /// <summary>
    /// オブジェクトグループ AoE を描画してよいか。攻略登録の手動ルール（hasManualRule）は
    /// ユーザーの明示的意思なので、自動推測テレグラフの抑制フラグ（show_auto_telegraphs=false）
    /// では殺さない。学習ベース（hasLearnedAoe）は従来通り自動扱いでフラグに従う。
    /// </summary>
    public static bool ShouldDrawObjectGroup(
        TriggerFile? file,
        int objectCount,
        bool hasLearnedAoe,
        int minGroupSize,
        bool hasManualRule = false)
    {
        if (objectCount <= 0)
        {
            return false;
        }

        if (!hasManualRule && !IsEnabled(file))
        {
            return false;
        }

        if ((hasLearnedAoe || hasManualRule) && objectCount == 1)
        {
            return true;
        }

        return objectCount >= Math.Max(1, minGroupSize);
    }

    public static bool ShouldDrawInstantActionTelegraph(TriggerFile? file)
    {
        // ActionUsedEvent は着弾後に届くため「予告」にはならないが、
        // ノーキャスト/オブジェクト起点の範囲を完全に見失わないため短い impact 表示だけ許可する。
        return IsEnabled(file);
    }

    public static AutoAoeArenaConfig ResolveArena(TriggerFile? file)
    {
        var profile = file is null ? null : StrategyPlanResolver.SelectActiveProfile(file);
        var shape = string.IsNullOrWhiteSpace(profile?.ArenaShape)
            ? "circle"
            : profile!.ArenaShape.ToLowerInvariant();
        var width = profile?.ArenaWidth;
        var depth = profile?.ArenaDepth;
        var radius = profile?.ArenaRadius ?? 20.0;

        if ((string.Equals(shape, "square", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(shape, "rect", StringComparison.OrdinalIgnoreCase)) &&
            (width is > 0 || depth is > 0))
        {
            radius = Math.Max(width ?? radius * 2, depth ?? radius * 2) * 0.5;
        }

        Vector3? lockedCenter = null;
        if (profile?.ArenaCenterX is { } centerX && profile.ArenaCenterZ is { } centerZ)
        {
            lockedCenter = new Vector3((float)centerX, 0f, (float)centerZ);
        }

        return new AutoAoeArenaConfig(
            ArenaRadius: radius,
            ArenaShape: shape,
            ArenaWidth: width,
            ArenaDepth: depth,
            LockedArenaCenter: lockedCenter);
    }
}
