using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using System.Globalization;
using FfxivEchoes.Capture;
using FfxivEchoes.Events;
using FfxivEchoes.Recording;
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

    private const float Width = 720f;
    private const float Height = 150f;
    private const float PastSeconds = 10f;
    private const float FutureSeconds = 30f;
    private const int MaxLabelChars = 14;

    private readonly CombatClock _combatClock;
    private readonly TriggerStore _store;
    private readonly RecordingScanner _recordings;
    private readonly SyncOffsetTracker _syncOffset;
    private readonly IDisposable _eventSub;
    private readonly List<HistoryEntry> _history = new();
    private readonly List<PredictedCast> _predictions = new();
    private readonly List<TimelineNoteRender> _timelineNotes = new();
    private readonly object _gate = new();

    private string _currentZone = "Unknown";
    private DisplayMode _mode = DisplayMode.Configured;

    public LiveTimelineWindow(IEventBus bus, CombatClock combatClock, TriggerStore store,
        RecordingScanner recordings, SyncOffsetTracker syncOffset)
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
        _recordings = recordings;
        _syncOffset = syncOffset;

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
                ReloadPredictions();
                UpdateVisibility();
                break;
            case CombatStartedEvent:
                _history.Clear();
                ReloadPredictions();
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

            // Timeline notes are resolved when predictions are reloaded.
            TimelineNoteRender[] timelineNotes;
            lock (_gate)
            {
                timelineNotes = _timelineNotes.ToArray();
            }

            foreach (var note in timelineNotes)
            {
                var t = note.RelativeSec + _syncOffset.CurrentOffsetSec;
                if (t < minSec || t > maxSec)
                {
                    continue;
                }

                var x = pos.X + (float)((t - minSec) / span) * width;
                draw.AddLine(new Vector2(x, pos.Y + height / 2f), new Vector2(x, pos.Y + height - 14f),
                    note.Color, 2f);

                if (note.Duration is { } dur && dur > 0)
                {
                    var x2 = pos.X + (float)((t + dur - minSec) / span) * width;
                    var barTop = pos.Y + height - 32f;
                    var barColor = (note.Color & 0x00FFFFFF) | 0x40000000;
                    draw.AddRectFilled(new Vector2(x, barTop), new Vector2(x2, barTop + 6f), barColor);
                }

                draw.AddText(new Vector2(x + 4f, pos.Y + height / 2f), note.Color, note.Label);
            }
        }

        // 録画ベースの予定キャスト（advance warning の主役）。
        // 縦方向に複数行で並べて重なりを避ける。ラベルは固定長で truncate。
        var showPredicted = triggerFile?.AutoSettings.ShowPredictedCasts ?? true;
        PredictedCast[] predictions;
        lock (_gate)
        {
            predictions = showPredicted ? _predictions.ToArray() : Array.Empty<PredictedCast>();
        }

        // 同期オフセットを各予測時刻に加算してから配置を決める
        var syncOffsetSec = _syncOffset.CurrentOffsetSec;

        // 未来側の予測：各イベントを 4 行に振り分けて重なりを軽減
        var predictedRowAssignments = AssignRows(predictions, p => p.RelativeSec + syncOffsetSec, minSec, maxSec, 4);
        for (var pi = 0; pi < predictions.Length; pi++)
        {
            var p = predictions[pi];
            var effectiveSec = p.RelativeSec + syncOffsetSec;
            if (effectiveSec < (float)nowSec - 1.0f) continue;
            if (effectiveSec < minSec || effectiveSec > maxSec) continue;
            var x = pos.X + (float)((effectiveSec - minSec) / span) * width;

            var distance = MathF.Max(0, (float)effectiveSec - (float)nowSec);
            var distanceAlpha = distance < 5.0f ? 0xE0u : (distance < 15.0f ? 0xA0u : 0x70u);
            var confidenceAlpha = (uint)Math.Clamp((int)MathF.Round((float)p.Confidence * 0xE0), 0x50, 0xE0);
            var alpha = Math.Min(distanceAlpha, confidenceAlpha);
            var fillColor = (alpha << 24) | 0x00A5FA60u;
            var strokeColor = (alpha << 24) | 0x00FFFA60u;

            var row = predictedRowAssignments[pi];
            var y = pos.Y + 26f + row * 16f;

            // 縦薄線（タイミング基準）
            DrawDashedVerticalLine(draw, new Vector2(x, pos.Y + 4f), pos.Y + height - 16f,
                (0x60u << 24) | (strokeColor & 0x00FFFFFFu), 1.0f, 3f);
            // ドット
            draw.AddCircleFilled(new Vector2(x, y + 6f), 3.5f, fillColor, 12);

            // ラベル（背景付きピル）
            var label = $"{Truncate(p.Label, MaxLabelChars)} {distance:0.0}s {FormatConfidence(p)}";
            DrawPillLabel(draw, new Vector2(x + 6f, y), label, fillColor, strokeColor, alpha);
        }

        // 履歴：時系列順に行を割り当て直して重なりを軽減
        HistoryEntry[] entries;
        lock (_gate)
        {
            entries = _history.ToArray();
        }
        var historyRowAssignments = AssignRows(entries, e => e.RelativeSec, minSec, maxSec, 4);
        for (var i = 0; i < entries.Length; i++)
        {
            var e = entries[i];
            if (e.RelativeSec < minSec || e.RelativeSec > maxSec) continue;
            var x = pos.X + (float)((e.RelativeSec - minSec) / span) * width;
            var row = historyRowAssignments[i];
            var y = pos.Y + 26f + row * 16f;
            draw.AddCircleFilled(new Vector2(x, y + 6f), 3.5f, e.Color, 12);
            var label = Truncate(e.Label, MaxLabelChars);
            DrawPillLabel(draw, new Vector2(x + 6f, y), label, e.Color, 0xFFE2E8F0, 0xC0);
        }

        // ウィンドウ末端のヘッダ
        ImGui.SetCursorScreenPos(pos);
        ImGui.Dummy(new Vector2(width, height));

        // 右上にモード表示と同期オフセット
        var modeLabel = _mode switch
        {
            DisplayMode.All => "MODE: ALL",
            DisplayMode.Configured => "MODE: CONFIGURED",
            _ => "MODE: HIDDEN",
        };
        draw.AddText(new Vector2(pos.X + width - 200f, pos.Y + 4f), 0xFF888888, modeLabel);

        if (Math.Abs(_syncOffset.CurrentOffsetSec) > 0.05)
        {
            var (label, _) = _syncOffset.LastSyncInfo;
            var syncTxt = $"SYNC: {_syncOffset.CurrentOffsetSec:+0.0;-0.0;0}s {label ?? ""}";
            draw.AddText(new Vector2(pos.X + width - 200f, pos.Y + 18f), 0xFF60D394, syncTxt);
        }
    }

    private readonly record struct HistoryEntry(double RelativeSec, string Label, uint Color);

    private readonly record struct PredictedCast(
        string EventType,
        double RelativeSec,
        string Label,
        int ObservedCount,
        double Confidence,
        double TimeJitterSeconds);

    private readonly record struct TimelineNoteRender(
        double RelativeSec,
        double? Duration,
        string Label,
        uint Color);

    private void ReloadPredictions()
    {
        AggregatedEvents? agg = null;
        try
        {
            agg = _recordings.Aggregate(_currentZone);
        }
        catch
        {
            // Recording files can be mid-write while combat starts.
        }

        var partyMembers = new HashSet<string>(
            _recordings.ListPartyMembers(_currentZone),
            StringComparer.OrdinalIgnoreCase);
        var predictions = agg is null
            ? Array.Empty<RecordingTimelinePrediction>()
            : RecordingPredictionPlanner.BuildTimelinePredictions(agg, partyMembers: partyMembers).ToArray();
        var notes = BuildTimelineNoteRenders(_store.GetByZone(_currentZone), agg);

        lock (_gate)
        {
            _predictions.Clear();
            foreach (var prediction in predictions)
            {
                _predictions.Add(new PredictedCast(
                    prediction.EventType,
                    prediction.RelativeSeconds,
                    prediction.Label,
                    prediction.ObservedCount,
                    prediction.Confidence,
                    prediction.TimeJitterSeconds));
            }

            _timelineNotes.Clear();
            _timelineNotes.AddRange(notes);
        }
    }

    private static List<TimelineNoteRender> BuildTimelineNoteRenders(TriggerFile? file, AggregatedEvents? agg)
    {
        var list = new List<TimelineNoteRender>();
        if (file is null)
        {
            return list;
        }

        foreach (var note in file.Notes.Concat(StrategyPlanResolver.BuildTimelineNotes(file)))
        {
            var noteTime = TimelineNoteResolver.ResolveTime(note, agg);
            if (noteTime is null)
            {
                continue;
            }

            var label = string.IsNullOrEmpty(note.Label) ? note.Id : note.Label;
            if (note.AttachedTo is not null)
            {
                label = "東 " + label;
            }

            list.Add(new TimelineNoteRender(
                RelativeSec: noteTime.Value,
                Duration: note.Duration,
                Label: label,
                Color: ParseColor(note.Color, 0xFFFCD34D)));
        }

        return list;
    }

    private static string FormatConfidence(PredictedCast prediction)
    {
        var percent = Math.Clamp((int)Math.Round(prediction.Confidence * 100.0), 0, 100);
        if (prediction.TimeJitterSeconds >= 0.5)
        {
            return $"{percent}% +/-{prediction.TimeJitterSeconds:0.0}s";
        }

        return $"{percent}%";
    }

    private static void DrawDashedVerticalLine(ImDrawListPtr draw, Vector2 top, float bottomY, uint color, float thickness, float dashPx)
    {
        var y = top.Y;
        while (y < bottomY)
        {
            var y2 = MathF.Min(y + dashPx, bottomY);
            draw.AddLine(new Vector2(top.X, y), new Vector2(top.X, y2), color, thickness);
            y = y2 + dashPx;
        }
    }

    /// <summary>
    /// 文字列を maxChars で切る（日本語混在も考慮して文字数で切る）。
    /// </summary>
    private static string Truncate(string s, int maxChars)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= maxChars) return s;
        return s[..maxChars] + "…";
    }

    /// <summary>
    /// イベントを N 行に振り分けて重なりを避ける。各行で「次のイベントが
    /// 近すぎる」場合は次の行に移す。シンプルな貪欲法。
    /// </summary>
    private static int[] AssignRows<T>(IReadOnlyList<T> events, Func<T, double> timeOf,
        float minSec, float maxSec, int numRows)
    {
        var result = new int[events.Count];
        if (events.Count == 0) return result;

        // 各行の「最後にラベルを置いた時刻」
        var lastTimePerRow = new double[numRows];
        for (var i = 0; i < numRows; i++) lastTimePerRow[i] = double.NegativeInfinity;

        // 時間順に並べてから割り当て（元の順序のために index 付き）
        var indices = new int[events.Count];
        for (var i = 0; i < events.Count; i++) indices[i] = i;
        Array.Sort(indices, (a, b) => timeOf(events[a]).CompareTo(timeOf(events[b])));

        // 1 ラベル分の最低時間幅（軸 40 秒で 1 行に置けるラベル数の目安）
        var span = MathF.Max(1f, maxSec - minSec);
        var minGapSec = span / 9.0; // 9 ラベルが 1 行に乗る程度

        foreach (var idx in indices)
        {
            var t = timeOf(events[idx]);
            var assigned = -1;
            for (var r = 0; r < numRows; r++)
            {
                if (t - lastTimePerRow[r] >= minGapSec)
                {
                    assigned = r;
                    break;
                }
            }
            if (assigned < 0)
            {
                // 全行詰まってる → 最も古い行を強制利用
                assigned = 0;
                var oldest = lastTimePerRow[0];
                for (var r = 1; r < numRows; r++)
                {
                    if (lastTimePerRow[r] < oldest) { oldest = lastTimePerRow[r]; assigned = r; }
                }
            }
            result[idx] = assigned;
            lastTimePerRow[assigned] = t;
        }
        return result;
    }

    /// <summary>
    /// ラベル背景の半透明丸囲い（pill）を描く。
    /// </summary>
    private static void DrawPillLabel(ImDrawListPtr draw, Vector2 textPos, string label,
        uint dotColor, uint textColor, uint alphaForBg)
    {
        var size = ImGui.CalcTextSize(label);
        var pad = new Vector2(4f, 1f);
        var bgRect1 = textPos - new Vector2(2f, 0f);
        var bgRect2 = textPos + size + pad * 2f - new Vector2(2f, 2f);
        var bgColor = ((alphaForBg / 2) << 24) | 0x00181C25u; // 半透明な暗背景
        draw.AddRectFilled(bgRect1, bgRect2, bgColor, 3f);
        draw.AddText(textPos + new Vector2(2f, 1f), textColor, label);
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
        return (0xFFu << 24) | (b << 16) | (g << 8) | r;
    }
}
