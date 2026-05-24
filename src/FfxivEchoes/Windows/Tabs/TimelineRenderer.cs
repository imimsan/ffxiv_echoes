using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using FfxivEchoes.Recording;

namespace FfxivEchoes.Windows.Tabs;

/// <summary>
/// 集計されたイベント群をレーン別の水平タイムラインとして描画する。
/// 同じレーンで時刻が近いイベントは縦方向にスタックして重なりを回避。
/// </summary>
public sealed class TimelineRenderer
{
    private const float SubRowHeight = 20f;
    private const float HeaderHeight = 28f;
    private const float DefaultPxPerSecond = 6f;
    private const float LaneLabelWidth = 110f;
    private const float BlockWidth = 90f;
    private const float MinBlockGapPx = 4f;
    private const int MaxSubRowsPerLane = 4;

    private static readonly (string Name, uint Color)[] Lanes =
    {
        ("ボスキャスト",   0xFF60A5FA),
        ("AA / アクション", 0xFFFBA67A),  // 通常攻撃 / 即時アクション専用
        ("ステータス",     0xFF34D399),
        ("HP 変化",        0xFFFBBF24),
        ("その他",         0xFFA78BFA),
    };

    private float _pxPerSecond = DefaultPxPerSecond;
    private string? _selectedKey;

    public EventKey? SelectedEvent { get; private set; }

    public void Draw(AggregatedEvents agg)
    {
        ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
        ImGui.SliderFloat("ピクセル/秒##timeline-zoom"u8, ref _pxPerSecond, 2.0f, 30.0f, "%.1f");
        ImGui.SameLine();
        ImGui.TextDisabled("（ズーム）");
        ImGui.Spacing();

        if (agg.Events.Count == 0)
        {
            ImGui.TextDisabled("録画がないため空です。");
            return;
        }

        var maxSeconds = 0.0;
        foreach (var ev in agg.Events)
        {
            if (ev.FirstSeenSeconds > maxSeconds) maxSeconds = ev.FirstSeenSeconds;
        }
        if (maxSeconds <= 0) maxSeconds = 60.0;
        var totalWidth = LaneLabelWidth + (float)maxSeconds * _pxPerSecond + 100f;

        // レーン振り分け + サブ行スタック
        var laneByEvent = new int[agg.Events.Count];
        var subRowByEvent = new int[agg.Events.Count];
        var subRowsPerLane = new int[Lanes.Length];

        // インデックスを時刻順にソートしてからサブ行を貪欲に割当
        var sortedIndices = Enumerable.Range(0, agg.Events.Count)
            .OrderBy(i => agg.Events[i].FirstSeenSeconds)
            .ToArray();

        var laneSubRowEndTime = new double[Lanes.Length, MaxSubRowsPerLane];
        for (var lane = 0; lane < Lanes.Length; lane++)
            for (var r = 0; r < MaxSubRowsPerLane; r++)
                laneSubRowEndTime[lane, r] = double.NegativeInfinity;

        foreach (var i in sortedIndices)
        {
            var ev = agg.Events[i];
            var lane = ResolveLane(ev.Key.Type);
            laneByEvent[i] = lane;

            var blockSec = (BlockWidth + MinBlockGapPx) / _pxPerSecond;
            var assigned = -1;
            for (var r = 0; r < MaxSubRowsPerLane; r++)
            {
                if (ev.FirstSeenSeconds >= laneSubRowEndTime[lane, r])
                {
                    assigned = r;
                    break;
                }
            }
            if (assigned < 0)
            {
                assigned = 0;
                var oldest = laneSubRowEndTime[lane, 0];
                for (var r = 1; r < MaxSubRowsPerLane; r++)
                {
                    if (laneSubRowEndTime[lane, r] < oldest)
                    {
                        oldest = laneSubRowEndTime[lane, r];
                        assigned = r;
                    }
                }
            }
            subRowByEvent[i] = assigned;
            laneSubRowEndTime[lane, assigned] = ev.FirstSeenSeconds + blockSec;
            if (assigned + 1 > subRowsPerLane[lane]) subRowsPerLane[lane] = assigned + 1;
        }

        // 各レーンの実高さを計算
        var laneTops = new float[Lanes.Length];
        var laneHeights = new float[Lanes.Length];
        var totalContentHeight = HeaderHeight;
        for (var lane = 0; lane < Lanes.Length; lane++)
        {
            laneTops[lane] = totalContentHeight;
            var rows = MathF.Max(1, subRowsPerLane[lane]);
            laneHeights[lane] = rows * SubRowHeight + 4f;
            totalContentHeight += laneHeights[lane];
        }
        totalContentHeight += 8f;

        var contentSize = new Vector2(totalWidth, totalContentHeight);
        if (ImGui.BeginChild("##timeline-canvas", contentSize, false,
            ImGuiWindowFlags.HorizontalScrollbar))
        {
            var draw = ImGui.GetWindowDrawList();
            var origin = ImGui.GetCursorScreenPos();

            DrawTimeAxis(draw, origin, maxSeconds, totalWidth);
            DrawLanes(draw, origin, totalWidth, laneTops, laneHeights);

            for (var i = 0; i < agg.Events.Count; i++)
            {
                var ev = agg.Events[i];
                DrawEventBlock(draw, origin, ev, laneByEvent[i], subRowByEvent[i],
                    laneTops[laneByEvent[i]]);
            }
        }
        ImGui.EndChild();
    }

    private static int ResolveLane(string type) => type switch
    {
        "cast_start" or "cast_complete" or "cast_cancel" => 0,
        "action_used" or "auto_attack" => 1, // AA / 即時アクション
        "status_gain" or "status_lose" or "status_update" => 2,
        "hp_change" => 3,
        _ => 4,
    };

    private void DrawTimeAxis(ImDrawListPtr draw, Vector2 origin, double maxSeconds, float totalWidth)
    {
        var top = origin.Y;
        var axisX = origin.X + LaneLabelWidth;
        // 10 秒ごとに目盛り、5 秒ごとに小目盛り
        for (var sec = 0; sec <= maxSeconds + 10; sec += 5)
        {
            var x = axisX + sec * _pxPerSecond;
            if (x > origin.X + totalWidth) break;
            var isMajor = sec % 10 == 0;
            var y2 = top + (isMajor ? HeaderHeight : HeaderHeight * 0.6f);
            draw.AddLine(new Vector2(x, top + 4f), new Vector2(x, y2), 0xFF555555, 1f);
            if (isMajor)
            {
                draw.AddText(new Vector2(x + 2f, top + 4f), 0xFFAAAAAA, $"{sec}s");
            }
        }
        // ヘッダ下の境界線
        draw.AddLine(new Vector2(origin.X, top + HeaderHeight),
                     new Vector2(origin.X + totalWidth, top + HeaderHeight),
                     0xFF666666, 1.0f);
    }

    private static void DrawLanes(ImDrawListPtr draw, Vector2 origin, float totalWidth,
        float[] laneTops, float[] laneHeights)
    {
        for (var i = 0; i < Lanes.Length; i++)
        {
            var top = origin.Y + laneTops[i];
            var height = laneHeights[i];
            var bg = (i % 2 == 0) ? 0x10FFFFFFu : 0x05FFFFFFu;
            draw.AddRectFilled(
                new Vector2(origin.X, top),
                new Vector2(origin.X + totalWidth, top + height),
                bg);
            draw.AddText(new Vector2(origin.X + 6f, top + 4f), 0xFFCCCCCC, Lanes[i].Name);
        }
    }

    private void DrawEventBlock(ImDrawListPtr draw, Vector2 origin, AggregatedEvent ev,
        int lane, int subRow, float laneTop)
    {
        var x = origin.X + LaneLabelWidth + (float)ev.FirstSeenSeconds * _pxPerSecond;
        var y = origin.Y + laneTop + 2f + subRow * SubRowHeight;
        var width = BlockWidth;
        var height = SubRowHeight - 2f;

        var rectMin = new Vector2(x, y);
        var rectMax = new Vector2(x + width, y + height);
        var color = Lanes[lane].Color;

        var mouse = ImGui.GetMousePos();
        var hovered = mouse.X >= rectMin.X && mouse.X <= rectMax.X
                   && mouse.Y >= rectMin.Y && mouse.Y <= rectMax.Y
                   && ImGui.IsWindowHovered();

        var fillColor = hovered ? Brighten(color) : color;
        draw.AddRectFilled(rectMin, rectMax, fillColor, 2f);
        // border
        draw.AddRect(rectMin, rectMax, 0x80000000, 2f);

        // テキスト：クリッピングして枠内に収める
        var label = ev.Key.Name ?? ev.Key.Id ?? ev.Key.Type;
        // 日本語混じりのため画素単位でカット
        draw.PushClipRect(rectMin, rectMax, true);
        draw.AddText(new Vector2(x + 4f, y + 1f), 0xFF000000, label);
        // 観測回数
        if (ev.Count > 1)
        {
            var countText = $"×{ev.Count}";
            var countSize = ImGui.CalcTextSize(countText);
            draw.AddText(new Vector2(rectMax.X - countSize.X - 4f, y + 1f), 0xFF000000, countText);
        }
        draw.PopClipRect();

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
