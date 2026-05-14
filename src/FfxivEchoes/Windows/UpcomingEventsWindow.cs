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
    private const int MaxItems = 8;
    private const float ImminentSec = 5f;
    private const float Width = 320f;
    private const float Height = 260f;

    private readonly CombatClock _combatClock;
    private readonly TriggerStore _store;
    private readonly RecordingScanner _recordings;
    private readonly SyncOffsetTracker _syncOffset;
    private readonly BranchObserverService? _branchObserver;
    private readonly IDisposable _eventSub;
    private readonly object _cacheGate = new();
    private readonly List<UpcomingTemplate> _cachedTemplates = new();
    private readonly Dictionary<string, UpcomingTemplate> _liveAutoAttackTemplates = new(StringComparer.OrdinalIgnoreCase);
    private bool _cacheDirty = true;

    private string _currentZone = "Unknown";
    private bool _inCombat;

    public UpcomingEventsWindow(
        IEventBus bus, CombatClock combatClock, TriggerStore store,
        RecordingScanner recordings, SyncOffsetTracker syncOffset,
        BranchObserverService? branchObserver = null)
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
        _branchObserver = branchObserver;

        Size = new Vector2(Width, Height) * ImGuiHelpers.GlobalScale;
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
                ClearLiveAutoAttacks();
                InvalidateCache();
                UpdateVisibility();
                break;
            case CombatStartedEvent:
                _inCombat = true;
                ClearLiveAutoAttacks();
                InvalidateCache();
                UpdateVisibility();
                break;
            case CombatEndedEvent:
                _inCombat = false;
                IsOpen = false;
                ClearLiveAutoAttacks();
                ClearCache();
                break;
            case BranchResolvedEvent:
                // 分岐確定 → タイムラインを再構築（rejected branch の mechanic を除外）
                InvalidateCache();
                break;
            case TriggerFiredEvent t when t.TriggerId.StartsWith("__auto_attack_timer_", StringComparison.Ordinal):
                TrackLiveAutoAttackTimer(t);
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

    private void ClearLiveAutoAttacks()
    {
        lock (_cacheGate)
        {
            _liveAutoAttackTemplates.Clear();
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
        DrawHeroAndList(nowRel.Value, items);
    }

    /// <summary>
    /// cactbot 系の縮むタイマーバー UI（BigWigs スタイル）。
    /// </summary>
    /// <remarks>
    /// 各イベントが横長のバーで、**時間が経つにつれてバーが右から左に縮む** → ゼロになる瞬間 = 発動。
    /// MMO プレイヤー（BigWigs / cactbot 経験者）にとって最も直感的な形式。
    /// 視覚特性：
    ///  - バー長 = 残り時間に直接対応（30 秒先 = 100% 長、5 秒先 = ~17% 長）
    ///  - 行背景は常に白、文字は常に黒（時間帯で文字色が変わると「直前まで見えない」問題が出るため）
    ///  - バーフィル色は残り時間で段階遷移：薄水（>15s）→ 黄（5-15s）→ オレンジ（1-5s）→ 赤（≤1s）
    ///  - 1 行 1 イベント。詳細はホバー時 tooltip
    ///  - 不明アクション・同名 ±3 秒重複は dedup
    /// </remarks>
    private void DrawHeroAndList(double nowRel, List<UpcomingItem> items)
    {
        var visible = items
            .Where(i => !UpcomingTimelinePolicy.IsRawUnknownActionLabel(i.Label))
            .Where(i => UpcomingTimelinePolicy.ShouldDisplayUpcomingItem(i.Time, nowRel))
            .ToList();
        visible = visible
            .OrderBy(i => i.Time)
            .ToList();
        visible = DedupByLabelWithin(visible, 3.0)
            .OrderBy(i => i.Time)
            .ToList();
        if (visible.Count == 0)
        {
            ImGui.TextDisabled("予測データなし — 録画してから 1 戦してください");
            return;
        }

        var scale = ImGuiHelpers.GlobalScale;
        var draw = ImGui.GetWindowDrawList();
        var winAvail = ImGui.GetContentRegionAvail();

        const float BarWindowSec = 30f;       // バー全長 = 30 秒先
        const int MaxRows = 8;
        var rowH = 22f * scale;
        var rowSpacing = 4f * scale;
        var headerH = 18f * scale;

        // 文字色は常に濃いグレー（黒）。背景白・バー色付きでも一貫して読める。
        // 「直前になってから白文字に切り替わる → 見えない」問題を解消。
        const uint TextColor = 0xFF1A1A1Au;
        // 行背景：ほぼ白（バー外領域も含めて全幅）
        const uint LaneBgColor = 0xFFF0F0F0u;
        // 行枠：薄いグレーで行間の区切りを補助
        const uint LaneBorderColor = 0xFFA0A0A0u;

        var groups = visible
            .GroupBy(i => UpcomingTimelinePolicy.SourceGroupName(i.Source, i.Label))
            .Select(g => new
            {
                Source = g.Key,
                Items = DedupByLabelWithin(g.ToList(), 3.0),
                FirstTime = g.Min(i => i.Time),
            })
            .OrderBy(g => g.FirstTime)
            .ToList();

        var rowIndex = 0;
        var rowsDrawn = 0;
        foreach (var group in groups)
        {
            if (rowsDrawn >= MaxRows)
            {
                break;
            }

            if (UpcomingTimelinePolicy.ShouldDrawSourceGroupHeader(group.Source))
            {
                var headerStart = ImGui.GetCursorScreenPos();
                var headerEnd = new Vector2(headerStart.X + winAvail.X, headerStart.Y + headerH);
                draw.AddRectFilled(headerStart, headerEnd, 0xCC101010u, 4f);
                draw.AddText(
                    new Vector2(headerStart.X + 8f * scale, headerStart.Y + 2f * scale),
                    0xFFE6E6E6u,
                    Truncate(group.Source, 26));
                ImGui.SetCursorScreenPos(new Vector2(headerStart.X, headerEnd.Y + 2f * scale));
            }

            foreach (var it in group.Items)
            {
                if (rowsDrawn >= MaxRows)
                {
                    break;
                }

            var remaining = (float)(it.Time - nowRel);
            // バー長：残時間 / 窓長。残時間多いほどバー長い（cactbot 流）。
            // 0 秒で完全に消える、過去（remaining < 0）は短い赤バーで余韻。
            var fillRatio = remaining > 0
                ? Math.Clamp(remaining / BarWindowSec, 0.02f, 1f)
                : 0.04f;

            // 色階層：cactbot の info/soon/alarm + 過ぎた赤。
            // 白背景にコントラストする色を選ぶ（薄水・黄でも視認できる）。
            uint barColor;
            if (remaining <= 1f)
            {
                barColor = 0xFF3030F0u;       // 真赤
            }
            else if (remaining <= 5f)
            {
                barColor = 0xFF3FA0F8u;       // 橙
            }
            else if (remaining <= 15f)
            {
                barColor = 0xFF50C8E8u;       // 黄
            }
            else
            {
                barColor = 0xFFD8C8B0u;       // 薄水
            }

            var rowStart = ImGui.GetCursorScreenPos();
            var rowEnd = new Vector2(rowStart.X + winAvail.X, rowStart.Y + rowH);
            var barWidth = (rowEnd.X - rowStart.X) * fillRatio;
            var barEnd = new Vector2(rowStart.X + barWidth, rowEnd.Y);

            // 背景レーン（白で全幅）→ 上に色付きバー（縮む）
            draw.AddRectFilled(rowStart, rowEnd, LaneBgColor, 4f);
            draw.AddRectFilled(rowStart, barEnd, barColor, 4f);
            // 行枠（白背景上で行を区切る薄いグレー枠）
            draw.AddRect(rowStart, rowEnd, LaneBorderColor, 4f, ImDrawFlags.None, 1f);

            // 直近 5 秒以内は赤い枠線で強調（"soon" / "alarm" 段階。
            // 白枠だと白背景に紛れるので赤に変更）。
            if (remaining <= 5f && remaining >= -1f)
            {
                draw.AddRect(rowStart, rowEnd, 0xFF3030F0u, 4f, ImDrawFlags.None, 2f);
            }

            // 時刻（バー左端、固定位置・1.05倍）
            var timeText = remaining < 0 ? $"+{(-remaining):F1}s" : $"{remaining:F1}s";
            ImGui.SetWindowFontScale(1.05f);
            var timeSize = ImGui.CalcTextSize(timeText);
            draw.AddText(
                new Vector2(rowStart.X + 8f * scale, rowStart.Y + (rowH - timeSize.Y) * 0.5f),
                TextColor, timeText);
            // 技名（時刻の右）
            var rowLabel = UpcomingTimelinePolicy.FormatRowLabel(it.Label, it.Source);
            var labelText = Truncate(rowLabel, 22);
            var labelSize = ImGui.CalcTextSize(labelText);
            draw.AddText(
                new Vector2(rowStart.X + 60f * scale, rowStart.Y + (rowH - labelSize.Y) * 0.5f),
                TextColor, labelText);
            ImGui.SetWindowFontScale(1f);

            // 行全体に当たり判定 → ホバー時に詳細 tooltip
            ImGui.SetCursorScreenPos(rowStart);
            ImGui.InvisibleButton($"##upcoming-row-{rowIndex}", new Vector2(winAvail.X, rowH));
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(rowLabel);
                if (!string.IsNullOrEmpty(it.Source))
                {
                    ImGui.TextDisabled($"source: {it.Source}");
                }
                ImGui.TextDisabled($"残り {timeText}");
                if (!string.IsNullOrEmpty(it.Sub))
                {
                    ImGui.TextDisabled(it.Sub);
                }
                ImGui.EndTooltip();
            }
            ImGui.SetCursorScreenPos(new Vector2(rowStart.X, rowEnd.Y + rowSpacing));
                rowsDrawn++;
                rowIndex++;
            }
        }
    }

    /// <summary>
    /// 同名イベントが指定秒内に並ぶケースを 1 件に集約（最も早いものを残す）。
    /// 例：cast_start 同名 0xAAAA / 0xBBBB がそれぞれ 12.8s / 13.2s で表示されると
    /// 「パラデイグマ」が 2 回並ぶ。これを 1 件にまとめる。
    /// </summary>
    private static List<UpcomingItem> DedupByLabelWithin(List<UpcomingItem> items, double windowSec)
    {
        if (items.Count <= 1) return items;
        var sorted = items.OrderBy(it => it.Time).ToList();
        var result = new List<UpcomingItem>(sorted.Count);
        var consumed = new bool[sorted.Count];
        for (var i = 0; i < sorted.Count; i++)
        {
            if (consumed[i]) continue;
            var head = sorted[i];
            for (var j = i + 1; j < sorted.Count; j++)
            {
                if (consumed[j]) continue;
                if (sorted[j].Time - head.Time > windowSec) break;
                if (UpcomingTimelinePolicy.ShouldDeduplicateDisplayItem(
                        head.EventType, sorted[j].EventType,
                        head.Label, sorted[j].Label,
                        head.Source, sorted[j].Source))
                {
                    consumed[j] = true; // 同名直後は隠す
                }
            }
            result.Add(SelectPreferredDuplicate(sorted, consumed, i, head, windowSec));
        }
        return result;
    }

    private static UpcomingItem SelectPreferredDuplicate(
        IReadOnlyList<UpcomingItem> sorted,
        bool[] consumed,
        int headIndex,
        UpcomingItem head,
        double windowSec)
    {
        var best = head;
        var bestScore = DuplicatePreferenceScore(best);
        for (var i = headIndex + 1; i < sorted.Count; i++)
        {
            if (!consumed[i] && sorted[i].Time - head.Time > windowSec) break;
            if (!UpcomingTimelinePolicy.ShouldDeduplicateDisplayItem(
                    head.EventType, sorted[i].EventType,
                    head.Label, sorted[i].Label,
                    head.Source, sorted[i].Source))
            {
                continue;
            }

            var score = DuplicatePreferenceScore(sorted[i]);
            if (score > bestScore ||
                (score == bestScore && sorted[i].Time < best.Time))
            {
                best = sorted[i];
                bestScore = score;
            }
        }

        return best;
    }

    private static int DuplicatePreferenceScore(UpcomingItem item)
    {
        var group = UpcomingTimelinePolicy.SourceGroupName(item.Source, item.Label);
        var score = UpcomingTimelinePolicy.IsCommonGroup(group) ? 0 : 10;
        if (string.Equals(item.EventType, "cast_start", StringComparison.OrdinalIgnoreCase))
        {
            score += 2;
        }
        if (string.Equals(item.EventType, "note", StringComparison.OrdinalIgnoreCase))
        {
            score += 1;
        }
        return score;
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
            var t = template.RelativeTime + (template.ApplySyncOffset ? offset : 0.0);
            if (!UpcomingTimelinePolicy.ShouldDisplayUpcomingItem(t, nowRel)) continue;
            list.Add(new UpcomingItem(
                Time: t,
                Icon: template.Icon,
                Label: template.Label,
                Sub: template.Sub,
                EventType: template.EventType,
                Source: template.Source,
                Color: template.Color));
        }

        UpcomingTemplate[] liveAutoAttacks;
        lock (_cacheGate)
        {
            var staleBefore = nowRel;
            foreach (var key in _liveAutoAttackTemplates
                         .Where(kv => kv.Value.RelativeTime <= staleBefore)
                         .Select(kv => kv.Key)
                         .ToArray())
            {
                _liveAutoAttackTemplates.Remove(key);
            }
            liveAutoAttacks = _liveAutoAttackTemplates.Values.ToArray();
        }

        foreach (var live in liveAutoAttacks)
        {
            list.RemoveAll(i =>
                UpcomingTimelinePolicy.IsAutoAttack(i.EventType) &&
                Math.Abs(i.Time - live.RelativeTime) <= 0.75);
        }
        foreach (var live in liveAutoAttacks)
        {
            if (!UpcomingTimelinePolicy.ShouldDisplayUpcomingItem(live.RelativeTime, nowRel)) continue;
            list.Add(new UpcomingItem(
                Time: live.RelativeTime,
                Icon: live.Icon,
                Label: live.Label,
                Sub: live.Sub,
                EventType: live.EventType,
                Source: live.Source,
                Color: live.Color));
        }

        list.Sort((a, b) => a.Time.CompareTo(b.Time));
        return list.Take(MaxItems).ToList();
    }

    private void TrackLiveAutoAttackTimer(TriggerFiredEvent ev)
    {
        if (!_inCombat)
        {
            return;
        }

        var duration = ExtractTimerDuration(ev);
        if (duration is null)
        {
            return;
        }

        var sourceEvent = ev.SourceEvent as ActionUsedEvent;
        var timestamp = sourceEvent?.Timestamp ?? ev.Timestamp;
        var baseRel = _combatClock.RelativeSecondsAt(timestamp);
        if (baseRel is null)
        {
            return;
        }

        var sourceId = sourceEvent?.SourceId ?? 0;
        var key = sourceId == 0 ? ev.TriggerId : $"aa:{sourceId}";
        var expectedRel = baseRel.Value + duration.Value;
        var sourceName = sourceEvent?.SourceName;
        var sub = string.IsNullOrWhiteSpace(sourceName) ? "live" : $"live: {sourceName}";
        lock (_cacheGate)
        {
            _liveAutoAttackTemplates[key] = new UpcomingTemplate(
                RelativeTime: expectedRel,
                Icon: "AA",
                Label: "AA",
                Sub: sub,
                EventType: "auto_attack",
                Source: sourceName,
                Color: 0xFFB6D9F0u,
                ApplySyncOffset: false);
        }
    }

    private static double? ExtractTimerDuration(TriggerFiredEvent ev)
    {
        foreach (var action in ev.Actions)
        {
            if (string.Equals(action.Type, "timer_bar", StringComparison.OrdinalIgnoreCase) &&
                action.Duration is { } duration &&
                duration > 0)
            {
                return duration;
            }
        }
        return null;
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
            // タイムラインは「キャストバー付きの予測技 + AA」だけに絞る。
            // includeActions=true だと action_used（ボスのインスタント技：phase 移行 / 連続短攻撃 等）
            // までタイムラインに乗ってしまい、「ソディアーク」「パラディグマ」のような同じ technic 名が
            // 3 回も並んでノイズになる。AA は別軸の重要情報なので明示的に残す。
            var predictions = RecordingPredictionPlanner.BuildTimelinePredictions(
                agg, partyMembers: partyMembers,
                includeActions: false,
                includeAutoAttacks: true);

            var displayPredictions = UpcomingTimelinePolicy.FilterDisplayPredictions(predictions);
            foreach (var prediction in displayPredictions)
            {
                var isAa = UpcomingTimelinePolicy.IsAutoAttack(prediction.EventType);
                var label = isAa ? "AA" : UpcomingTimelinePolicy.FormatRowLabel(prediction.Label, prediction.Source);
                var color = isAa
                    ? 0xFFB6D9F0u                                 // AA：淡い青
                    : 0xFFFAA560u;                                // cast_start：オレンジ
                list.Add(new UpcomingTemplate(
                    RelativeTime: prediction.RelativeSeconds,
                    Icon: isAa ? "AA" : GuessIconForCast(label),
                    Label: label,
                    Sub: FormatPredictionSub(prediction),
                    EventType: prediction.EventType,
                    Source: prediction.Source,
                    Color: color));
            }
        }

        var file = _store.GetByZone(_currentZone);
        if (file is not null)
        {
            // 分岐確定状態を反映：rejected branch の mechanic はタイムラインから除外、
            // pending（未確定）の branch も非表示で「共通 mechanic だけ見せる」既定ポリシー。
            Func<string?, bool>? branchCheck = _branchObserver is { } bo
                ? bo.IsActiveOrCommon
                : null;
            var strategyNotes = StrategyPlanResolver.BuildTimelineNotes(file, branchCheck);
            foreach (var note in file.Notes.Concat(strategyNotes))
            {
                var resolved = TimelineNoteResolver.ResolveTime(note, agg);
                if (resolved is null) continue;
                var icon = note.Icons.FirstOrDefault() ?? "東";
                list.Add(new UpcomingTemplate(
                    RelativeTime: resolved.Value,
                    Icon: icon,
                    Label: note.Label,
                    Sub: note.Role is { Length: > 0 } r ? $"role: {r}" : "note",
                    EventType: "note",
                    Source: null,
                    Color: 0xFF34D34Du));
            }
        }

        list.Sort((a, b) => a.RelativeTime.CompareTo(b.RelativeTime));
        return list;
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

    /// <summary>
    /// 行下段の sub テキスト。視覚ノイズを避けるため、デフォルトで信頼度 100% / 0x ID は隠す。
    /// </summary>
    /// <remarks>
    /// 旧表示「cast_start:0x67BF / 100%」のように毎行に技術情報が並んで読みづらかった。
    /// 表示ポリシー：
    /// ・信頼度が 100% より低い場合だけ %、それ未満は 1 戦のみで観測など信用度低い指標として有用
    /// ・タイミングのブレ（jitter）が 0.5 秒以上ある場合だけ ±X.Xs 表示
    /// ・上記どちらも無ければ sub は空（行高だけ消費しない）
    /// </remarks>
    private static string FormatPredictionSub(RecordingTimelinePrediction prediction)
    {
        var percent = Math.Clamp((int)Math.Round(prediction.Confidence * 100.0), 0, 100);
        var parts = new List<string>(2);
        if (percent < 100)
        {
            parts.Add($"信頼 {percent}%");
        }
        if (prediction.TimeJitterSeconds >= 0.5)
        {
            parts.Add($"±{prediction.TimeJitterSeconds:0.0}s");
        }
        return string.Join(" / ", parts);
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
        string EventType,
        string? Source,
        uint Color);

    private readonly record struct UpcomingTemplate(
        double RelativeTime,
        string Icon,
        string Label,
        string Sub,
        string EventType,
        string? Source,
        uint Color,
        bool ApplySyncOffset = true);
}
