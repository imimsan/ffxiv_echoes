using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;

namespace FfxivEchoes.Windows;

/// <summary>
/// ワールド座標を画面に投影して描画する透明オーバーレイ。
/// screen_arrow / field_marker の描画先（SPEC.md §5.1）。
/// </summary>
public sealed class WorldOverlayWindow : Window, IDisposable
{
    private readonly IGameGui _gameGui;
    private readonly IObjectTable _objectTable;
    private readonly List<ArrowItem> _arrows = new();
    private readonly List<MarkerItem> _markers = new();
    private readonly object _gate = new();

    public WorldOverlayWindow(IGameGui gameGui, IObjectTable objectTable)
        : base("##ffxiv-echoes-world-overlay",
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoBringToFrontOnFocus |
            ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoMouseInputs |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoSavedSettings)
    {
        _gameGui = gameGui;
        _objectTable = objectTable;
        IsOpen = false;
        RespectCloseHotkey = false;
    }

    public void Dispose() { }

    public void AddArrow(bool fromSelf, Vector3? from, Vector3 to, string? colorHex, double durationSec)
    {
        var ttl = durationSec <= 0 ? 5.0 : durationSec;
        var item = new ArrowItem(
            FromSelf: fromSelf,
            From: from ?? Vector3.Zero,
            To: to,
            Color: ParseColor(colorHex, 0xFF00FF00),
            ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(ttl));
        lock (_gate)
        {
            _arrows.Add(item);
        }
        IsOpen = true;
    }

    public void AddMarker(Vector3 worldPos, string? shape, float radius, string? colorHex, double durationSec)
    {
        var ttl = durationSec <= 0 ? 5.0 : durationSec;
        var item = new MarkerItem(
            WorldPos: worldPos,
            Shape: (shape ?? "circle").ToLowerInvariant(),
            Radius: MathF.Max(0.5f, radius),
            Color: ParseColor(colorHex, 0xFF00FF00),
            ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(ttl));
        lock (_gate)
        {
            _markers.Add(item);
        }
        IsOpen = true;
    }

    public override void PreDraw()
    {
        // 全画面サイズに展開
        var io = ImGui.GetIO();
        ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
        ImGui.SetNextWindowSize(io.DisplaySize, ImGuiCond.Always);
    }

    public override void Draw()
    {
        var now = DateTimeOffset.UtcNow;
        ArrowItem[] arrows;
        MarkerItem[] markers;
        lock (_gate)
        {
            _arrows.RemoveAll(a => a.ExpiresAt <= now);
            _markers.RemoveAll(m => m.ExpiresAt <= now);
            arrows = _arrows.ToArray();
            markers = _markers.ToArray();
        }

        if (arrows.Length == 0 && markers.Length == 0)
        {
            IsOpen = false;
            return;
        }

        var draw = ImGui.GetForegroundDrawList();

        var localPlayer = _objectTable.LocalPlayer;
        var selfWorld = localPlayer is not null
            ? new Vector3(localPlayer.Position.X, localPlayer.Position.Y, localPlayer.Position.Z)
            : Vector3.Zero;

        foreach (var arrow in arrows)
        {
            var fromWorld = arrow.FromSelf ? selfWorld : arrow.From;
            if (!_gameGui.WorldToScreen(fromWorld, out var fromScreen)) continue;
            if (!_gameGui.WorldToScreen(arrow.To, out var toScreen)) continue;
            DrawArrow(draw, fromScreen, toScreen, arrow.Color);
        }

        foreach (var marker in markers)
        {
            if (!_gameGui.WorldToScreen(marker.WorldPos, out var center)) continue;
            DrawMarker(draw, center, marker);
        }
    }

    private static void DrawArrow(ImDrawListPtr draw, Vector2 from, Vector2 to, uint color)
    {
        draw.AddLine(from, to, color, 3f);
        // 簡易矢じり（直線の終端）
        var dir = to - from;
        var len = dir.Length();
        if (len < 1f) return;
        dir /= len;
        var perp = new Vector2(-dir.Y, dir.X);
        const float headLen = 14f;
        const float headWidth = 8f;
        var tip = to;
        var baseCenter = to - dir * headLen;
        var left = baseCenter + perp * headWidth;
        var right = baseCenter - perp * headWidth;
        draw.AddTriangleFilled(tip, left, right, color);
    }

    private static void DrawMarker(ImDrawListPtr draw, Vector2 center, MarkerItem marker)
    {
        // radius は world 単位なので画面サイズには直接マップできない。固定サイズで描画。
        var px = 24f;
        switch (marker.Shape)
        {
            case "x_mark":
                draw.AddLine(center + new Vector2(-px, -px), center + new Vector2(px, px), marker.Color, 3f);
                draw.AddLine(center + new Vector2(-px, px), center + new Vector2(px, -px), marker.Color, 3f);
                break;
            case "square":
                draw.AddRect(center + new Vector2(-px, -px), center + new Vector2(px, px), marker.Color, 0f, ImDrawFlags.None, 3f);
                break;
            case "arrow":
                draw.AddTriangleFilled(
                    center + new Vector2(0, -px),
                    center + new Vector2(-px * 0.7f, px * 0.5f),
                    center + new Vector2(px * 0.7f, px * 0.5f),
                    marker.Color);
                break;
            case "circle":
            default:
                draw.AddCircle(center, px, marker.Color, 24, 3f);
                draw.AddCircleFilled(center, 4f, marker.Color);
                break;
        }
    }

    private static uint ParseColor(string? color, uint fallback)
    {
        if (string.IsNullOrEmpty(color) || color.Length != 7 || color[0] != '#')
        {
            return fallback;
        }
        if (!uint.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return fallback;
        }
        var r = (rgb >> 16) & 0xFF;
        var g = (rgb >> 8) & 0xFF;
        var b = rgb & 0xFF;
        // ImGui は ABGR
        return (0xFFu << 24) | (b << 16) | (g << 8) | r;
    }

    private sealed record ArrowItem(bool FromSelf, Vector3 From, Vector3 To, uint Color, DateTimeOffset ExpiresAt);
    private sealed record MarkerItem(Vector3 WorldPos, string Shape, float Radius, uint Color, DateTimeOffset ExpiresAt);
}
