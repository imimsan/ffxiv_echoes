using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using System.Globalization;
using FfxivEchoes.Capture;
using FfxivEchoes.Events;
using FfxivEchoes.Triggers;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Windows;

/// <summary>
/// 戦闘中のライブタイムライン HUD（SPEC.md §8）。
/// </summary>
/// <remarks>
/// 戦闘相対秒を中央ラインとし、左に過去 10 秒、右に未来 30 秒を描画。
/// SyncPoints と直近の TriggerFiredEvent / CastStartedEvent を時系列に並べる。
/// 表示モード（all / configured / hide）はスラッシュコマンドで切替可能。
/// </remarks>
public sealed class LiveTimelineWindow : Window, IDisposable
{
    public enum DisplayMode { Configured, All, Hidden }

    private const float Width = 640f;
    private const float Height = 110f;
    private const float PastSeconds = 10f;
    private const float FutureSeconds = 30f;

    private readonly CombatClock _combatClock;
    private readonly TriggerStore _store;
    private readonly IDisposable _eventSub;
    private readonly List<HistoryEntry> _history = new();
    private readonly object _gate = new();

    private string _currentZone = "Unknown";
    private DisplayMode _mode = DisplayMode.Configured;

    public LiveTimelineWindow(IEventBus bus, CombatClock combatClock, TriggerStore store)
        : base("##ffxiv-echoes-live-timeline",
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoBringToFrontOnFocus |
            ImGuiWindowFlags.NoNav)
    {
        _combatClock = combatClock;
        _store = store;

        Size = new Vector2(Width, Height);
        SizeCondition = ImGuiCond.FirstUseEver;
        IsOpen = false;
        RespectCloseHotkey = false;

        _eventSub = bus.SubscribeAll(OnEvent);
    }

    public void Dispose() => _eventSub.Dispose();

    public DisplayMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            UpdateVisibility();
        }
    }

    private void OnEvent(IGameEvent ev)
    {
        switch (ev)
        {
            case ZoneChangedEvent z:
                _currentZone = z.ZoneName;
                _history.Clear();
                UpdateVisibility();
                break;
            case CombatStartedEvent:
                _history.Clear();
                UpdateVisibility();
                break;
            case CombatEndedEvent:
                // 戦闘終了：履歴は残してウィンドウは閉じる（再戦時に履歴がクリアされる）
                IsOpen = false;
                break;
            case CastStartedEvent cs:
                AddHistory(ev.Timestamp, $"⚡ {cs.SourceName} → {cs.CastActionName}", 0xFF60A5FA);
                break;
            case TriggerFiredEvent tf:
                AddHistory(ev.Timestamp, $"🎯 {tf.TriggerName ?? tf.TriggerId}", 0xFFFBBF24);
                break;
        }
    }

    private void AddHistory(DateTimeOffset at, string label, uint color)
    {
        var t = _combatClock.RelativeSecondsAt(at);
        if (t is null)
        {
            return;
        }
        lock (_gate)
        {
            _history.Add(new HistoryEntry(t.Value, label, color));
            // 古いものは捨てる（30 秒以上前）
            var threshold = t.Value - 60.0;
            _history.RemoveAll(h => h.RelativeSec < threshold);
        }
    }

    private void UpdateVisibility()
    {
        if (_mode == DisplayMode.Hidden)
        {
            IsOpen = false;
            return;
        }
        // 戦闘中 + ファイル設定で show_timeline = true なら表示
        var file = _store.GetByZone(_currentZone);
        var shouldShow = _combatClock.InCombat && (file?.AutoSettings.ShowTimeline ?? true);
        if (_mode == DisplayMode.All)
        {
            shouldShow = _combatClock.InCombat;
        }
        IsOpen = shouldShow;
    }

    public override void PreDraw()
    {
        UpdateVisibility();
    }

    public override void Draw()
    {
        var nowSec = _combatClock.RelativeSecondsAt(DateTimeOffset.UtcNow) ?? 0;
        var draw = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail();
        var width = MathF.Max(200f, avail.X);
        var height = MathF.Max(60f, avail.Y);

        // 軸範囲
        var minSec = (float)nowSec - PastSeconds;
        var maxSec = (float)nowSec + FutureSeconds;
        var span = maxSec - minSec;

        // 背景
        draw.AddRectFilled(pos, pos + new Vector2(width, height), 0xC0202020, 4f);

        // 過去/未来の境界（現在ライン）
        var xNow = pos.X + (float)((nowSec - minSec) / span) * width;
        draw.AddLine(new Vector2(xNow, pos.Y), new Vector2(xNow, pos.Y + height), 0xFFE2E8F0, 2f);

        // 5 秒刻みの目盛り
        for (var s = (int)Math.Floor(minSec / 5.0) * 5; s <= maxSec; s += 5)
        {
            var x = pos.X + (s - minSec) / span * width;
            draw.AddLine(new Vector2(x, pos.Y + height - 12f),
                new Vector2(x, pos.Y + height), 0xFF606060, 1f);
            var label = $"{s}s";
            draw.AddText(new Vector2(x + 2f, pos.Y + height - 14f), 0xFF888888, label);
        }

        // SyncPoints
        var triggerFile = _store.GetByZone(_currentZone);
        if (triggerFile is not null)
        {
            foreach (var sp in triggerFile.SyncPoints)
            {
                if (sp.ExpectedTime < minSec || sp.ExpectedTime > maxSec)
                {
                    continue;
                }
                var x = pos.X + (float)((sp.ExpectedTime - minSec) / span) * width;
                draw.AddLine(new Vector2(x, pos.Y + 10f), new Vector2(x, pos.Y + height - 14f),
                    0xFF4ADE80, 2f);
                draw.AddText(new Vector2(x + 4f, pos.Y + 10f), 0xFF4ADE80, sp.Id);
            }

            // タイムラインノート（軽減/LB 等のメモ）
            foreach (var note in triggerFile.Notes)
            {
                if (note.Time < minSec || note.Time > maxSec)
                {
                    continue;
                }
                var noteColor = ParseColor(note.Color, 0xFFFCD34D); // amber
                var x = pos.X + (float)((note.Time - minSec) / span) * width;
                // ノート用のラインは下半分に。SyncPoints と区別。
                draw.AddLine(new Vector2(x, pos.Y + height / 2f), new Vector2(x, pos.Y + height - 14f),
                    noteColor, 2f);

                // 長尺ノートはバー幅で示す
                if (note.Duration is { } dur && dur > 0)
                {
                    var x2 = pos.X + (float)((note.Time + dur - minSec) / span) * width;
                    var barTop = pos.Y + height - 32f;
                    var barColor = (noteColor & 0x00FFFFFF) | 0x40000000;
                    draw.AddRectFilled(new Vector2(x, barTop), new Vector2(x2, barTop + 6f), barColor);
                }

                var label = string.IsNullOrEmpty(note.Label) ? note.Id : note.Label;
                draw.AddText(new Vector2(x + 4f, pos.Y + height / 2f), noteColor, label);
            }
        }

        // 履歴
        HistoryEntry[] entries;
        lock (_gate)
        {
            entries = _history.ToArray();
        }
        for (var i = 0; i < entries.Length; i++)
        {
            var e = entries[i];
            if (e.RelativeSec < minSec || e.RelativeSec > maxSec)
            {
                continue;
            }
            var x = pos.X + (float)((e.RelativeSec - minSec) / span) * width;
            var y = pos.Y + 26f + (i % 3) * 18f;
            draw.AddCircleFilled(new Vector2(x, y), 4f, e.Color);
            draw.AddText(new Vector2(x + 6f, y - 6f), 0xFFE2E8F0, e.Label);
        }

        // ウィンドウ末端のヘッダ
        ImGui.SetCursorScreenPos(pos);
        ImGui.Dummy(new Vector2(width, height));

        // 右上にモード表示
        var modeLabel = _mode switch
        {
            DisplayMode.All => "MODE: ALL",
            DisplayMode.Configured => "MODE: CONFIGURED",
            _ => "MODE: HIDDEN",
        };
        draw.AddText(new Vector2(pos.X + width - 130f, pos.Y + 4f), 0xFF888888, modeLabel);
    }

    private readonly record struct HistoryEntry(double RelativeSec, string Label, uint Color);

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
        return (0xFFu << 24) | (b << 16) | (g << 8) | r;
    }
}
