using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace FfxivEchoes.Windows;

/// <summary>
/// 俯瞰アリーナ図（ミニマップ）。ギミック発生時に安置/危険ゾーンを上面図で表示する。
/// </summary>
/// <remarks>
/// デモ（demo/demo.js の renderArena）の SVG 描画を ImGui DrawList で再実装したもの。
/// gimmick タイプ：outer_ring / inner_circle / scatter / stack / cone。
/// アクションハンドラ（ArenaViewHandler）から AddArenaView で項目を追加し、
/// duration が経過したら自動で消える。何も無くなったらウィンドウを閉じる。
/// </remarks>
public sealed class MinimapWindow : Window, IDisposable
{
    private const float ArenaSize = 200f;
    private const float CalloutHeight = 44f;
    private const float Margin = 8f;

    // 色（RGBA32 little-endian の AABBGGRR 形式 = ImGui の慣習）
    private const uint ColBg = 0xD8141A22;          // arena 背景
    private const uint ColBorder = 0x40FFFFFF;       // arena 外周
    private const uint ColDanger = 0x6B7171F8;       // 危険（赤）
    private const uint ColDangerLine = 0xFF7171F8;
    private const uint ColSafe = 0x8077C534;         // 安置（緑）
    private const uint ColSafeLine = 0xFF7BD391;
    private const uint ColScatter = 0x6BFAA560;      // 散開ポジ（青）
    private const uint ColScatterLine = 0xFFFAA560;
    private const uint ColBoss = 0xFF7C92FB;         // ボス（オレンジ＝ABGR）
    private const uint ColBossDanger = 0xFF7171F8;   // ボス危険状態（赤）
    private const uint ColText = 0xFFFFFFFF;
    private const uint ColCallout = 0xFF24BFFB;      // callout 黄色（amber, ABGR）
    private const uint ColCalloutShadow = 0xFF000000;

    private readonly List<ArenaItem> _items = new();
    private readonly object _gate = new();

    public MinimapWindow()
        : base("##ffxiv-echoes-minimap",
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoBringToFrontOnFocus |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoScrollbar)
    {
        var scale = ImGuiHelpers.GlobalScale;
        Size = new Vector2(ArenaSize + Margin * 2, ArenaSize + CalloutHeight + Margin * 2) * scale;
        SizeCondition = ImGuiCond.FirstUseEver;
        IsOpen = false;
        RespectCloseHotkey = false;
    }

    public void Dispose() { }

    /// <summary>
    /// ギミックを 1 件追加。duration 秒経過すると自動で消える。
    /// </summary>
    public void AddArenaView(string gimmick, string? callout, double durationSec, string? direction, double? fanDeg)
    {
        if (string.IsNullOrEmpty(gimmick))
        {
            return;
        }
        var ttl = durationSec <= 0 ? 5.0 : durationSec;
        var item = new ArenaItem(
            Gimmick: gimmick.ToLowerInvariant(),
            Callout: callout ?? string.Empty,
            Direction: direction,
            FanDeg: fanDeg ?? 90.0,
            ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(ttl));
        lock (_gate)
        {
            _items.Add(item);
        }
        IsOpen = true;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
        }
        IsOpen = false;
    }

    public override void Draw()
    {
        var now = DateTimeOffset.UtcNow;

        ArenaItem? top = null;
        lock (_gate)
        {
            _items.RemoveAll(it => it.ExpiresAt <= now);
            // 残り時間が短い（先に解決する）ギミックを優先表示
            for (var i = 0; i < _items.Count; i++)
            {
                if (top is null || _items[i].ExpiresAt < top.Value.ExpiresAt)
                {
                    top = _items[i];
                }
            }
        }

        if (top is null)
        {
            IsOpen = false;
            return;
        }

        var item = top.Value;
        var draw = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var scale = ImGuiHelpers.GlobalScale;
        var size = ArenaSize * scale;
        var center = new Vector2(pos.X + size * 0.5f, pos.Y + size * 0.5f);
        var r = size * 0.5f - 4f * scale;

        DrawArena(draw, center, r);
        DrawGimmickBody(draw, center, r, item);
        DrawBoss(draw, center, scale, item);

        // 描画領域を確保（ImGui のレイアウトを進める）
        ImGui.Dummy(new Vector2(size, size));

        // Callout
        var remaining = (item.ExpiresAt - now).TotalSeconds;
        var calloutText = item.Callout;
        var subText = $"{Math.Max(0, remaining):0.0}s";
        DrawCallout(draw, pos, size, scale, calloutText, subText);
        ImGui.Dummy(new Vector2(size, CalloutHeight * scale));
    }

    // ── 描画ヘルパ ─────────────────────────────────────────────────

    private static void DrawArena(ImDrawListPtr draw, Vector2 center, float r)
    {
        draw.AddCircleFilled(center, r, ColBg, 64);
        draw.AddCircle(center, r, ColBorder, 64, 1.5f);
    }

    private static void DrawGimmickBody(ImDrawListPtr draw, Vector2 center, float r, ArenaItem item)
    {
        switch (item.Gimmick)
        {
            case "outer_ring":
                // 外周が危険、中央安置
                draw.AddCircleFilled(center, r * 0.96f, ColDanger, 64);
                draw.AddCircleFilled(center, r * 0.45f, ColSafe, 48);
                DrawDashedCircle(draw, center, r * 0.45f, ColSafeLine, 2.5f, 24);
                AddCenteredText(draw, center + new Vector2(0, r * 0.62f), "SAFE", ColSafeLine, 1.05f);
                break;

            case "inner_circle":
                // 中央が危険、外周安置
                draw.AddCircleFilled(center, r * 0.55f, ColDanger, 48);
                draw.AddCircle(center, r * 0.55f, ColDangerLine, 48, 2f);
                AddCenteredText(draw, center, "!", ColText, 1.6f);
                AddCenteredText(draw, center + new Vector2(0, r * 0.78f), "外周安置", ColSafeLine, 0.95f);
                break;

            case "scatter":
                {
                    var positions = new (float dx, float dy, string label)[]
                    {
                        (0, -0.72f, "N"),
                        (0.72f, 0, "E"),
                        (0, 0.72f, "S"),
                        (-0.72f, 0, "W"),
                    };
                    foreach (var p in positions)
                    {
                        var c = new Vector2(center.X + p.dx * r, center.Y + p.dy * r);
                        draw.AddCircleFilled(c, r * 0.15f, ColScatter, 24);
                        draw.AddCircle(c, r * 0.15f, ColScatterLine, 24, 2f);
                        AddCenteredText(draw, c, p.label, ColText, 1.0f);
                    }
                    break;
                }

            case "stack":
                draw.AddCircleFilled(center, r * 0.42f, ColScatter, 48);
                DrawDashedCircle(draw, center, r * 0.42f, ColScatterLine, 2.5f, 20);
                AddCenteredText(draw, center + new Vector2(0, r * 0.62f), "STACK", ColScatterLine, 1.0f);
                break;

            case "cone":
                {
                    var angle = ParseDirectionAngle(item.Direction) * MathF.PI / 180f;
                    var halfFan = (float)(item.FanDeg * Math.PI / 360.0);
                    var segments = 24;
                    var path = new List<Vector2> { center };
                    for (var i = 0; i <= segments; i++)
                    {
                        var t = (float)i / segments;
                        var a = angle - halfFan + (halfFan * 2f) * t;
                        path.Add(new Vector2(center.X + MathF.Cos(a) * r, center.Y + MathF.Sin(a) * r));
                    }
                    foreach (var p in path)
                    {
                        draw.PathLineTo(p);
                    }
                    draw.PathFillConvex(ColDanger);
                    // 外周線
                    for (var i = 0; i <= segments; i++)
                    {
                        var t = (float)i / segments;
                        var a = angle - halfFan + (halfFan * 2f) * t;
                        draw.PathLineTo(new Vector2(center.X + MathF.Cos(a) * r, center.Y + MathF.Sin(a) * r));
                    }
                    draw.PathStroke(ColDangerLine, ImDrawFlags.None, 1.5f);
                    break;
                }

            default:
                AddCenteredText(draw, center, $"unknown: {item.Gimmick}", ColText, 0.9f);
                break;
        }
    }

    private static void DrawBoss(ImDrawListPtr draw, Vector2 center, float scale, ArenaItem item)
    {
        var bossR = 6f * scale;
        var color = item.Gimmick == "scatter" ? ColBossDanger : ColBoss;
        draw.AddCircleFilled(center, bossR, color, 16);
        draw.AddCircle(center, bossR, ColText, 16, 1.5f);
    }

    private static void DrawCallout(ImDrawListPtr draw, Vector2 winPos, float size, float scale, string callout, string sub)
    {
        var top = winPos.Y + size + 4f * scale;
        var bgRect1 = new Vector2(winPos.X, top);
        var bgRect2 = new Vector2(winPos.X + size, top + (CalloutHeight - 4f) * scale);
        draw.AddRectFilled(bgRect1, bgRect2, 0xC8000000, 4f);
        draw.AddRect(bgRect1, bgRect2, 0x30FFFFFF, 4f, ImDrawFlags.None, 1f);

        if (!string.IsNullOrEmpty(callout))
        {
            ImGui.PushFont(ImGui.GetFont());
            var textSize = ImGui.CalcTextSize(callout) * 1.15f;
            var textPos = new Vector2(
                bgRect1.X + (size - textSize.X) * 0.5f,
                bgRect1.Y + 4f * scale);
            // 影
            draw.AddText(textPos + new Vector2(1, 1), ColCalloutShadow, callout);
            draw.AddText(textPos, ColCallout, callout);
            ImGui.PopFont();
        }

        if (!string.IsNullOrEmpty(sub))
        {
            var subSize = ImGui.CalcTextSize(sub);
            var subPos = new Vector2(
                bgRect1.X + (size - subSize.X) * 0.5f,
                bgRect2.Y - subSize.Y - 4f * scale);
            draw.AddText(subPos, 0xFFAAAAAA, sub);
        }
    }

    private static void DrawDashedCircle(ImDrawListPtr draw, Vector2 center, float r, uint color, float thickness, int dashes)
    {
        var step = MathF.PI * 2f / dashes;
        for (var i = 0; i < dashes; i++)
        {
            if ((i & 1) == 1) continue;
            var a1 = i * step;
            var a2 = (i + 1) * step;
            var p1 = new Vector2(center.X + MathF.Cos(a1) * r, center.Y + MathF.Sin(a1) * r);
            var p2 = new Vector2(center.X + MathF.Cos(a2) * r, center.Y + MathF.Sin(a2) * r);
            draw.AddLine(p1, p2, color, thickness);
        }
    }

    private static void AddCenteredText(ImDrawListPtr draw, Vector2 center, string text, uint color, float scale)
    {
        if (string.IsNullOrEmpty(text)) return;
        var size = ImGui.CalcTextSize(text) * scale;
        var pos = new Vector2(center.X - size.X * 0.5f, center.Y - size.Y * 0.5f);
        // 影
        draw.AddText(pos + new Vector2(1, 1), 0xFF000000, text);
        draw.AddText(pos, color, text);
    }

    private static float ParseDirectionAngle(string? direction)
    {
        // SVG 座標系（Y 下向き）に合わせて、N = -90°、E = 0°、S = 90°、W = 180°
        return direction?.ToUpperInvariant() switch
        {
            "N" => -90f,
            "NE" => -45f,
            "E" => 0f,
            "SE" => 45f,
            "S" => 90f,
            "SW" => 135f,
            "W" => 180f,
            "NW" => -135f,
            _ => -90f,
        };
    }

    private readonly record struct ArenaItem(
        string Gimmick,
        string Callout,
        string? Direction,
        double FanDeg,
        DateTimeOffset ExpiresAt);
}
