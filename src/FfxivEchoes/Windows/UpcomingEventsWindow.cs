using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using FfxivEchoes.Capture;
using FfxivEchoes.Events;
using FfxivEchoes.Recording;
using FfxivEchoes.Triggers;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Windows;

/// <summary>
/// 戦闘中に「次に来るイベント」を縦並びで表示する HUD。
/// デモ（demo/demo.js renderUpcoming）と同じ視覚的レイアウトを ImGui で再現。
/// 録画予測 + ノートを混ぜて時系列順に並べる。同期オフセット適用済み。
/// </summary>
public sealed class UpcomingEventsWindow : Window, IDisposable
{
    private const int MaxItems = 6;
    private const float ImminentSec = 5f;
    private const float Width = 340f;

    private readonly CombatClock _combatClock;
    private readonly TriggerStore _store;
    private readonly RecordingScanner _recordings;
    private readonly SyncOffsetTracker _syncOffset;
    private readonly IDisposable _eventSub;
    private readonly object _cacheGate = new();
    private readonly List<UpcomingTemplate> _cachedTemplates = new();
    private bool _cacheDirty = true;

    private string _currentZone = "Unknown";
    private bool _inCombat;

    public UpcomingEventsWindow(
        IEventBus bus, CombatClock combatClock, TriggerStore store,
        RecordingScanner recordings, SyncOffsetTracker syncOffset)
        : base("##ffxiv-echoes-upcoming",
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoBringToFrontOnFocus |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse)
    {
        _combatClock = combatClock;
        _store = store;
        _recordings = recordings;
        _syncOffset = syncOffset;

        Size = new Vector2(Width, 200f) * ImGuiHelpers.GlobalScale;
        SizeCondition = ImGuiCond.FirstUseEver;
        IsOpen = false;
        RespectCloseHotkey = false;

        _eventSub = bus.SubscribeAll(OnEvent);
    }

    public void Dispose() => _eventSub.Dispose();

    private void OnEvent(IGameEvent ev)
    {
        switch (ev)
        {
            case ZoneChangedEvent z:
                _currentZone = string.IsNullOrEmpty(z.ZoneName) ? "Unknown" : z.ZoneName;
                InvalidateCache();
                UpdateVisibility();
                break;
            case CombatStartedEvent:
                _inCombat = true;
                InvalidateCache();
                UpdateVisibility();
                break;
            case CombatEndedEvent:
                _inCombat = false;
                IsOpen = false;
                ClearCache();
                break;
        }
    }

    private void InvalidateCache()
    {
        lock (_cacheGate)
        {
            _cacheDirty = true;
        }
    }

    private void ClearCache()
    {
        lock (_cacheGate)
        {
            _cachedTemplates.Clear();
            _cacheDirty = true;
        }
    }

    private void UpdateVisibility()
    {
        var file = _store.GetByZone(_currentZone);
        // ライブHUD と同じく show_timeline と連動
        var shouldShow = _inCombat && (file?.AutoSettings.ShowTimeline ?? true);
        IsOpen = shouldShow;
    }

    public override void PreDraw()
    {
        UpdateVisibility();
    }

    public override void Draw()
    {
        var nowRel = _combatClock.RelativeSecondsAt(DateTimeOffset.UtcNow);
        if (nowRel is null)
        {
            ImGui.TextDisabled("（戦闘外）");
            return;
        }

        var items = CollectUpcoming(nowRel.Value);
        if (items.Count == 0)
        {
            ImGui.TextDisabled("予測データがありません。録画してから再戦闘してください。");
            return;
        }

        var iconColumnW = 32f * ImGuiHelpers.GlobalScale;
        var countdownColumnW = 64f * ImGuiHelpers.GlobalScale;
        var rowHeight = 28f * ImGuiHelpers.GlobalScale;

        foreach (var it in items)
        {
            var remaining = (float)(it.Time - nowRel.Value);
            var imminent = remaining <= ImminentSec && remaining >= -0.5f;

            var draw = ImGui.GetWindowDrawList();
            var rowStart = ImGui.GetCursorScreenPos();
            var rowEnd = new Vector2(rowStart.X + ImGui.GetContentRegionAvail().X, rowStart.Y + rowHeight);

            // 背景
            uint bg = imminent ? 0x60249EFB : 0x80000000; // amber when imminent / 黒半透明
            uint border = imminent ? 0xFF3B82F6 : 0xFF888888; // 青 / 灰
            // border_left を太く
            draw.AddRectFilled(rowStart, rowEnd, bg, 3f);
            draw.AddLine(new Vector2(rowStart.X, rowStart.Y), new Vector2(rowStart.X, rowEnd.Y), it.Color, 4f);

            // アイコン領域
            var iconX = rowStart.X + 8f;
            var iconY = rowStart.Y + (rowHeight - 22f * ImGuiHelpers.GlobalScale) * 0.5f;
            // emoji icon を文字列として描画（フォントが対応していれば見える）
            draw.AddText(new Vector2(iconX, iconY), 0xFFFFFFFF, it.Icon);

            // カウントダウン
            var countdownText = remaining < 0
                ? $"+{(-remaining):0.0}s"
                : $"{remaining:0.0}s";
            var cdColor = imminent ? 0xFF3BCFFB : 0xFFFFFFFFu;
            var cdSize = ImGui.CalcTextSize(countdownText);
            var cdX = rowStart.X + iconColumnW + countdownColumnW - cdSize.X - 4f;
            var cdY = rowStart.Y + (rowHeight - cdSize.Y) * 0.5f;
            draw.AddText(new Vector2(cdX, cdY), cdColor, countdownText);

            // ラベル
            var labelX = rowStart.X + iconColumnW + countdownColumnW + 6f;
            var labelY = rowStart.Y + 3f;
            draw.AddText(new Vector2(labelX, labelY), 0xFFE2E8F0, Truncate(it.Label, 22));
            // sub
            if (!string.IsNullOrEmpty(it.Sub))
            {
                draw.AddText(new Vector2(labelX, labelY + 13f * ImGuiHelpers.GlobalScale), 0xFF94A3B8, Truncate(it.Sub, 28));
            }

            ImGui.Dummy(new Vector2(ImGui.GetContentRegionAvail().X, rowHeight + 2f));
        }
    }

    private List<UpcomingItem> CollectUpcoming(double nowRel)
    {
        EnsureCache();

        UpcomingTemplate[] templates;
        lock (_cacheGate)
        {
            templates = _cachedTemplates.ToArray();
        }

        var list = new List<UpcomingItem>();
        var offset = _syncOffset.CurrentOffsetSec;
        foreach (var template in templates)
        {
            var t = template.RelativeTime + offset;
            if (t < nowRel - 1.0) continue;
            list.Add(new UpcomingItem(
                Time: t,
                Icon: template.Icon,
                Label: template.Label,
                Sub: template.Sub,
                Color: template.Color));
        }

        list.Sort((a, b) => a.Time.CompareTo(b.Time));
        return list.Take(MaxItems).ToList();
    }

    private void EnsureCache()
    {
        lock (_cacheGate)
        {
            if (!_cacheDirty)
            {
                return;
            }

            _cacheDirty = false;
        }

        var built = BuildUpcomingTemplates();

        lock (_cacheGate)
        {
            _cachedTemplates.Clear();
            _cachedTemplates.AddRange(built);
        }
    }

    private List<UpcomingTemplate> BuildUpcomingTemplates()
    {
        var list = new List<UpcomingTemplate>();
        AggregatedEvents? agg = null;

        try
        {
            agg = _recordings.Aggregate(_currentZone);
        }
        catch
        {
            // Recording files can be mid-write during combat. Keep the HUD alive.
        }

        if (agg is not null)
        {
            var partyMembers = new HashSet<string>(
                _recordings.ListPartyMembers(_currentZone),
                StringComparer.OrdinalIgnoreCase);
            var predictions = RecordingPredictionPlanner.BuildTimelinePredictions(agg, partyMembers: partyMembers);

            foreach (var prediction in predictions)
            {
                var label = prediction.Label;
                list.Add(new UpcomingTemplate(
                    RelativeTime: prediction.RelativeSeconds,
                    Icon: GuessIconForCast(label),
                    Label: label,
                    Sub: FormatPredictionSub(prediction),
                    Color: 0xFFFAA560));
            }
        }

        var file = _store.GetByZone(_currentZone);
        if (file is not null)
        {
            foreach (var note in file.Notes.Concat(StrategyPlanResolver.BuildTimelineNotes(file)))
            {
                var resolved = TimelineNoteResolver.ResolveTime(note, agg);
                if (resolved is null) continue;
                var icon = note.Icons.FirstOrDefault() ?? "東";
                list.Add(new UpcomingTemplate(
                    RelativeTime: resolved.Value,
                    Icon: icon,
                    Label: note.Label,
                    Sub: note.Role is { Length: > 0 } r ? $"role: {r}" : "note",
                    Color: 0xFF34D34Du));
            }
        }

        list.Sort((a, b) => a.RelativeTime.CompareTo(b.RelativeTime));
        return list;
    }

    private List<UpcomingItem> CollectUpcomingSlow(double nowRel)
    {
        var list = new List<UpcomingItem>();
        var offset = _syncOffset.CurrentOffsetSec;

        // 1. 録画ベースの予測キャスト
        try
        {
            var agg = _recordings.Aggregate(_currentZone);
            var partyMembers = new HashSet<string>(_recordings.ListPartyMembers(_currentZone),
                StringComparer.OrdinalIgnoreCase);
            var predictions = RecordingPredictionPlanner.BuildTimelinePredictions(agg, partyMembers: partyMembers);
            foreach (var prediction in predictions)
            {
                var t = prediction.RelativeSeconds + offset;
                if (t < nowRel - 1.0) continue;
                var label = prediction.Label;
                list.Add(new UpcomingItem(
                    Time: t,
                    Icon: GuessIconForCast(label),
                    Label: label,
                    Sub: FormatPredictionSub(prediction),
                    Color: 0xFFFAA560));
            }
        }
        catch { }

        // 2. ノート（軽減/LB 等）
        var file = _store.GetByZone(_currentZone);
        if (file is not null)
        {
            AggregatedEvents? aggForNotes = null;
            try { aggForNotes = _recordings.Aggregate(_currentZone); } catch { }

            foreach (var note in file.Notes.Concat(StrategyPlanResolver.BuildTimelineNotes(file)))
            {
                var resolved = TimelineNoteResolver.ResolveTime(note, aggForNotes);
                if (resolved is null) continue;
                var t = resolved.Value + offset;
                if (t < nowRel - 1.0) continue;
                var icon = note.Icons.FirstOrDefault() ?? "📌";
                list.Add(new UpcomingItem(
                    Time: t,
                    Icon: icon,
                    Label: note.Label,
                    Sub: note.Role is { Length: > 0 } r ? $"role: {r}" : "note",
                    Color: 0xFF34D34Du));
            }
        }

        list.Sort((a, b) => a.Time.CompareTo(b.Time));
        return list.Take(MaxItems).ToList();
    }

    private static string GuessIconForCast(string castName)
    {
        if (string.IsNullOrEmpty(castName)) return "⚡";
        // 単純なキーワードマッチでアイコン推測
        if (castName.Contains("肥大") || castName.Contains("ロア") || castName.Contains("爆発")) return "💥";
        if (castName.Contains("追跡") || castName.Contains("散開")) return "🎯";
        if (castName.Contains("集合") || castName.Contains("シェア")) return "🤝";
        if (castName.Contains("コーン") || castName.Contains("薙ぎ") || castName.Contains("ブレス")) return "🗡";
        if (castName.Contains("波動") || castName.Contains("AoE")) return "🌀";
        if (castName.Contains("ヒート") || castName.Contains("ファイア")) return "🔥";
        if (castName.Contains("ウィング") || castName.Contains("飛")) return "🪽";
        return "⚡";
    }

    private static string FormatPredictionSub(RecordingTimelinePrediction prediction)
    {
        var percent = Math.Clamp((int)Math.Round(prediction.Confidence * 100.0), 0, 100);
        var jitter = prediction.TimeJitterSeconds >= 0.5
            ? $" / +/-{prediction.TimeJitterSeconds:0.0}s"
            : string.Empty;
        var id = string.IsNullOrEmpty(prediction.Id) ? prediction.EventType : $"{prediction.EventType}:{prediction.Id}";
        return $"{id} / {percent}%{jitter}";
    }

    private static string Truncate(string s, int maxChars)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= maxChars) return s;
        return s[..maxChars] + "…";
    }

    private readonly record struct UpcomingItem(
        double Time,
        string Icon,
        string Label,
        string Sub,
        uint Color);

    private readonly record struct UpcomingTemplate(
        double RelativeTime,
        string Icon,
        string Label,
        string Sub,
        uint Color);
}
