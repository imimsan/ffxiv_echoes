using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace FfxivEchoes.Windows;

/// <summary>
/// ライブ中のオーバーレイ（中央大型テキスト + タイマーバー）。
/// </summary>
/// <remarks>
/// ImGui の Window 機能で実装。NoDecoration / NoBackground / NoMouseInputs で
/// クリックスルーかつ装飾なしの軽量オーバーレイにする。
/// 表示中の項目が無くなったら IsOpen を false にして描画コストをゼロに戻す。
/// </remarks>
public sealed class OverlayWindow : Window, IDisposable
{
    private static readonly Vector4 DefaultTextColor = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 DefaultBarColor = new(1f, 0.66f, 0f, 1f);
    private static readonly Vector4 WarnColor = new(1f, 0.3f, 0.3f, 1f);

    private readonly List<TextItem> _texts = new();
    private readonly List<TimerBarItem> _timerBars = new();
    private readonly object _gate = new();

    public OverlayWindow()
        : base("##ffxiv-echoes-overlay",
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoBringToFrontOnFocus |
            ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoMouseInputs |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.AlwaysAutoResize)
    {
        IsOpen = false;
        ForceMainWindow = false;
        RespectCloseHotkey = false;
    }

    public void Dispose() { }

    // ─── 追加 API（アクションハンドラから呼ばれる） ───────────────────

    public void AddText(string text, double durationSec, string? colorHex, string? size)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        var ttl = durationSec <= 0 ? 5.0 : durationSec;
        var item = new TextItem(
            Text: text,
            Color: ParseColor(colorHex, DefaultTextColor),
            Scale: ResolveScale(size),
            ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(ttl));
        lock (_gate)
        {
            _texts.Add(item);
        }
        IsOpen = true;
    }

    public void AddTimerBar(string label, double durationSec, string? colorHex, double? warnAt)
    {
        if (durationSec <= 0)
        {
            return;
        }
        var item = new TimerBarItem(
            Label: label ?? string.Empty,
            Color: ParseColor(colorHex, DefaultBarColor),
            StartedAt: DateTimeOffset.UtcNow,
            DurationSec: durationSec,
            WarnAt: warnAt);
        lock (_gate)
        {
            _timerBars.Add(item);
        }
        IsOpen = true;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _texts.Clear();
            _timerBars.Clear();
        }
        IsOpen = false;
    }

    // ─── レンダリング ────────────────────────────────────────────────

    public override void PreDraw()
    {
        // 画面中央付近に固定（X = 中央、Y = 上 1/3）。後で設定可能にしたい。
        var io = ImGui.GetIO();
        var center = new Vector2(io.DisplaySize.X * 0.5f, io.DisplaySize.Y * 0.30f);
        ImGui.SetNextWindowPos(center, ImGuiCond.Always, new Vector2(0.5f, 0.5f));
    }

    public override void Draw()
    {
        var now = DateTimeOffset.UtcNow;

        TextItem[] texts;
        TimerBarItem[] bars;
        lock (_gate)
        {
            _texts.RemoveAll(t => t.ExpiresAt <= now);
            _timerBars.RemoveAll(b => b.RemainingSeconds(now) <= 0);
            texts = _texts.ToArray();
            bars = _timerBars.ToArray();
        }

        if (texts.Length == 0 && bars.Length == 0)
        {
            IsOpen = false;
            return;
        }

        foreach (var t in texts)
        {
            DrawText(t);
        }

        if (texts.Length > 0 && bars.Length > 0)
        {
            ImGui.Spacing();
        }

        foreach (var b in bars)
        {
            DrawTimerBar(b, now);
        }
    }

    private static void DrawText(TextItem item)
    {
        ImGui.SetWindowFontScale(item.Scale);
        ImGui.PushStyleColor(ImGuiCol.Text, item.Color);
        // 中央寄せ：テキスト幅を測ってカーソルを補正
        var size = ImGui.CalcTextSize(item.Text);
        var avail = ImGui.GetContentRegionAvail();
        var indent = MathF.Max(0, (avail.X - size.X) * 0.5f);
        if (indent > 0)
        {
            ImGui.Indent(indent);
        }
        ImGui.TextUnformatted(item.Text);
        if (indent > 0)
        {
            ImGui.Unindent(indent);
        }
        ImGui.PopStyleColor();
        ImGui.SetWindowFontScale(1.0f);
    }

    private static void DrawTimerBar(TimerBarItem item, DateTimeOffset now)
    {
        var remaining = item.RemainingSeconds(now);
        var fraction = (float)Math.Clamp(remaining / item.DurationSec, 0.0, 1.0);
        var color = item.WarnAt is { } warn && remaining <= warn ? WarnColor : item.Color;

        if (!string.IsNullOrEmpty(item.Label))
        {
            ImGui.TextUnformatted($"{item.Label} {remaining:0.0}s");
        }

        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, color);
        ImGui.ProgressBar(fraction, new Vector2(240f * ImGuiHelpers.GlobalScale, 14f), string.Empty);
        ImGui.PopStyleColor();
    }

    // ─── 補助 ────────────────────────────────────────────────────────

    private static Vector4 ParseColor(string? color, Vector4 fallback)
    {
        if (string.IsNullOrEmpty(color) || color.Length != 7 || color[0] != '#')
        {
            return fallback;
        }
        if (!uint.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return fallback;
        }
        return new Vector4(
            ((rgb >> 16) & 0xFF) / 255f,
            ((rgb >> 8) & 0xFF) / 255f,
            (rgb & 0xFF) / 255f,
            1.0f);
    }

    private static float ResolveScale(string? size) => size?.ToLowerInvariant() switch
    {
        "small" => 1.5f,
        "medium" => 2.0f,
        "large" => 3.0f,
        _ => 2.5f,
    };

    private sealed record TextItem(string Text, Vector4 Color, float Scale, DateTimeOffset ExpiresAt);

    private sealed record TimerBarItem(
        string Label,
        Vector4 Color,
        DateTimeOffset StartedAt,
        double DurationSec,
        double? WarnAt)
    {
        public double RemainingSeconds(DateTimeOffset now) =>
            DurationSec - (now - StartedAt).TotalSeconds;
    }
}
