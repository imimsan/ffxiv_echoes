using System;
using System.Collections.Generic;

namespace FfxivEchoes.Windows;

/// <summary>デバフ HUD の 1 行分（Dalamud 非依存。テスト可能）。</summary>
public readonly record struct DebuffRow(
    uint StatusId,
    string Name,
    float RemainingSec,
    ushort Stacks);

/// <summary>デバフ HUD の表示整形・選別の純粋ロジック。</summary>
public static class DebuffHudPolicy
{
    /// <summary>残り秒の表示書式。10秒未満は 0.1 秒精度（直前の判断に効く）、永続（&lt;=0）は空。</summary>
    public static string FormatRemaining(float remainingSec)
    {
        if (remainingSec <= 0f)
        {
            return string.Empty;
        }
        if (remainingSec < 10f)
        {
            return $"{remainingSec:0.0}s";
        }
        if (remainingSec < 60f)
        {
            return $"{(int)remainingSec}s";
        }
        var m = (int)(remainingSec / 60f);
        var s = (int)(remainingSec % 60f);
        return $"{m}m{s:00}s";
    }

    /// <summary>
    /// 表示行を選別・整列する。残り秒昇順（切れそうなものが上）、永続（remaining&lt;=0）は末尾。
    /// output バッファ再利用方式（フレーム毎 alloc 回避）。
    /// </summary>
    public static void SelectDisplayRows(IReadOnlyList<DebuffRow> rows, List<DebuffRow> output)
    {
        output.Clear();
        for (var i = 0; i < rows.Count; i++)
        {
            output.Add(rows[i]);
        }
        output.Sort(static (a, b) =>
        {
            var aPerm = a.RemainingSec <= 0f;
            var bPerm = b.RemainingSec <= 0f;
            if (aPerm != bPerm)
            {
                return aPerm ? 1 : -1;
            }
            return a.RemainingSec.CompareTo(b.RemainingSec);
        });
    }
}
