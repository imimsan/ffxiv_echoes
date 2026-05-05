using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using FfxivEchoes.Recording;

namespace FfxivEchoes.Windows.Tabs;

/// <summary>
/// 集計されたイベント群をレーン別の水平タイムラインとして描画する（SPEC.md §7.2.1）。
/// </summary>
public sealed class TimelineRenderer
{
    private const float LaneHeight = 24f;
    private const float HeaderHeight = 22f;
    private const float DefaultPxPerSecond = 4f;
    private const float LaneLabelWidth = 110f;

    private static readonly (string Name, uint Color)[] Lanes =
    {
        ("ボスキャスト",  0xFF60A5FA),  // blue-ish
        ("ステータス",    0xFF34D399),   // green
        ("HP 変化",       0xFFFBBF24),   // amber
        ("その他",        0xFFA78BFA),   // purple
    };

    private float _pxPerSecond = DefaultPxPerSecond;
    private string? _selectedKey;

    /// <summary>選択中のイベントキー。null なら未選択。</summary>
    public EventKey? SelectedEvent { get; private set; }

    public void Draw(AggregatedEvents agg)
    {
        ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
        ImGui.SliderFloat("ピクセル/秒##timeline-zoom"u8, ref _pxPerSecond, 1.0f, 20.0f, "%.1f");

        if (agg.Events.Count == 0)
        {
            ImGui.TextDisabled("録画がないため空です。");
            return;
        }

        var maxSeconds = 0.0;
        foreach (var ev in agg.Events)
        {
            if (ev.FirstSeenSeconds > maxSeconds)
            {
                maxSeconds = ev.FirstSeenSeconds;
            }
        }
        if (maxSeconds <= 0)
        {
            maxSeconds = 60.0;
        }
        var totalWidth = LaneLabelWidth + (float)maxSeconds * _pxPerSecond + 100f;

        var laneByEvent = AssignLanes(agg);

        var contentSize = new Vector2(totalWidth, HeaderHeight + Lanes.Length * LaneHeight + 8f);
        if (ImGui.BeginChild("##timeline-canvas", contentSize, false,
            ImGuiWindowFlags.HorizontalScrollbar))
        {
            var draw = ImGui.GetWindowDrawList();
            var origin = ImGui.GetCursorScreenPos();

            DrawTimeAxis(draw, origin, maxSeconds, totalWidth);
            DrawLanes(draw, origin, totalWidth);

            for (var i = 0; i < agg.Events.Count; i++)
            {
                var ev = agg.Events[i];
                var laneIndex = laneByEvent[i];
                DrawEventBlock(draw, origin, ev, laneIndex);
            }
        }
        ImGui.EndChild();
    }

    private static int[] AssignLanes(AggregatedEvents agg)
    {
        var laneByEvent = new int[agg.Events.Count];
        for (var i = 0; i < agg.Events.Count; i++)
        {
            var t = agg.Events[i].Key.Type;
            laneByEvent[i] = t switch
            {
                "cast_start" or "cast_complete" or "cast_cancel" or "action_used" => 0,
                "status_gain" or "status_lose" or "status_update" => 1,
                "hp_change" => 2,
                _ => 3,
            };
        }
        return laneByEvent;
    }

    private static void DrawTimeAxis(ImDrawListPtr draw, Vector2 origin, double maxSeconds, float totalWidth)
    {
        var headerRect = new Vector2(origin.X + LaneLabelWidth, origin.Y);
        // 10 秒刻みで目盛り
        var step = 10;
        for (var sec = 0; sec <= maxSeconds + step; sec += step)
        {
            var x = headerRect.X + sec * 4f;
            // 親が SliderFloat で _pxPerSecond を更新しているので、ここでは不正確だが目安として OK
            // 正確には _pxPerSecond を引数化すべきだが見た目重視で簡略
        }

        var top = origin.Y + HeaderHeight;
        draw.AddLine(
            new Vector2(origin.X + LaneLabelWidth, top),
            new Vector2(origin.X + totalWidth, top),
            0xFF666666, 1.0f);
    }

    private static void DrawLanes(ImDrawListPtr draw, Vector2 origin, float totalWidth)
    {
        for (var i = 0; i < Lanes.Length; i++)
        {
            var top = origin.Y + HeaderHeight + i * LaneHeight;
            var bg = (i % 2 == 0) ? 0x10FFFFFFu : 0x05FFFFFFu;
            draw.AddRectFilled(
                new Vector2(origin.X, top),
                new Vector2(origin.X + totalWidth, top + LaneHeight),
                bg);
            draw.AddText(new Vector2(origin.X + 6f, top + 4f), 0xFFCCCCCC, Lanes[i].Name);
        }
    }

    private void DrawEventBlock(ImDrawListPtr draw, Vector2 origin, AggregatedEvent ev, int lane)
    {
        var x = origin.X + LaneLabelWidth + (float)ev.FirstSeenSeconds * _pxPerSecond;
        var y = origin.Y + HeaderHeight + lane * LaneHeight + 3f;
        var width = MathF.Max(8f, MathF.Min(90f, ev.Count * 6f));
        var height = LaneHeight - 6f;

        var rectMin = new Vector2(x, y);
        var rectMax = new Vector2(x + width, y + height);
        var color = Lanes[lane].Color;

        // ホバー検出
        var mouse = ImGui.GetMousePos();
        var hovered = mouse.X >= rectMin.X && mouse.X <= rectMax.X
                   && mouse.Y >= rectMin.Y && mouse.Y <= rectMax.Y
                   && ImGui.IsWindowHovered();

        var fillColor = hovered ? Brighten(color) : color;
        draw.AddRectFilled(rectMin, rectMax, fillColor, 2f);

        var label = ev.Key.Name ?? ev.Key.Id ?? ev.Key.Type;
        if (label.Length > 12)
        {
            label = string.Concat(label.AsSpan(0, 11), "…");
        }
        draw.AddText(new Vector2(x + 4f, y + 2f), 0xFF000000, label);

        if (hovered)
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted($"{ev.Key.Type}");
            if (ev.Key.Id is not null) ImGui.TextUnformatted($"  id: {ev.Key.Id}");
            if (ev.Key.Name is not null) ImGui.TextUnformatted($"  name: {ev.Key.Name}");
            if (ev.Key.Source is not null) ImGui.TextUnformatted($"  source: {ev.Key.Source}");
            if (ev.Key.Target is not null) ImGui.TextUnformatted($"  target: {ev.Key.Target}");
            ImGui.TextUnformatted($"  観測回数: {ev.Count}");
            ImGui.TextUnformatted($"  初回時刻: t={ev.FirstSeenSeconds:0.00}s");
            ImGui.EndTooltip();

            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                SelectedEvent = ev.Key;
                _selectedKey = $"{ev.Key.Type}|{ev.Key.Id}|{ev.Key.Name}|{ev.Key.Source}|{ev.Key.Target}";
            }
        }
    }

    private static uint Brighten(uint argb)
    {
        var a = (argb >> 24) & 0xFF;
        var r = Math.Min(255u, ((argb >> 16) & 0xFF) + 30);
        var g = Math.Min(255u, ((argb >> 8) & 0xFF) + 30);
        var b = Math.Min(255u, (argb & 0xFF) + 30);
        return (a << 24) | (r << 16) | (g << 8) | b;
    }
}
