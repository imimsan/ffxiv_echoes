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
    // 2 体フェーズで両ボスの予定を時系列順に拾えるよう余裕を持たせる（描画段で各カラム/グループが
    // 個別に行数を制限する）。8 だと先頭切り捨てで片方のボスが丸ごと落ちることがあった。
    private const int MaxItems = 16;
    private const float ImminentSec = 5f;
    private const float Width = 320f;
    private const float Height = 260f;

    private readonly CombatClock _combatClock;
    private readonly TriggerStore _store;
    private readonly RecordingScanner _recordings;
    private readonly SyncOffsetTracker _syncOffset;
    private readonly BranchObserverService? _branchObserver;
    private readonly CurrentPhaseTracker? _phaseTracker;
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
        BranchObserverService? branchObserver = null,
        CurrentPhaseTracker? phaseTracker = null)
        // NoResize は付けない：2 体フェーズで各カラムが狭く技名が読み切れないとき、
        // ユーザーがウィンドウを横に広げて可読性を確保できるようにする（広げた幅は imgui.ini に永続）。
        : base("##ffxiv-echoes-upcoming",
            ImGuiWindowFlags.NoTitleBar |
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
        _phaseTracker = phaseTracker;

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
            case PhaseTransitionedEvent:
                // フェーズ遷移 → タイムラインを再構築（過去フェーズの mechanic を除外）
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

        // 表示中の非共通ソース（＝ボス）数で 1 体 / 2 体モードを切り替える。
        // 2 体同時詠唱フェーズでは左右 2 カラムに分け、どちらのボスの技かを空間的に分離する。
        var distinctBosses = visible
            .Select(i => UpcomingTimelinePolicy.SourceGroupName(i.Source, i.Label))
            .Where(s => !UpcomingTimelinePolicy.IsCommonGroup(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // ちょうど 2 体のときだけ 2 カラム。3 体以上（クローン多体・add 混在・未解決ソース）は
        // 2 カラムに畳むと 3 体目以降が左カラムへ無言混入し左ボス名・色で誤帰属する（発生源取り違え）ため、
        // source 別グループ見出しで正しくラベルする単一カラムへフォールバックする。
        var twoBoss = distinctBosses.Count == 2;
        if (twoBoss)
        {
            // 左右の割り当ては「次の最速詠唱を持つボス」順（＝時系列依存）だとフレームごとに左右が
            // 入れ替わり目が迷う。名前で安定ソートして、同じ 2 体なら常に同じ側に固定する。
            distinctBosses.Sort(StringComparer.OrdinalIgnoreCase);
        }

        var scale = ImGuiHelpers.GlobalScale;
        var width = ImGui.GetContentRegionAvail().X;

        DrawPhaseHeader(scale, width, twoBoss);

        if (twoBoss)
        {
            DrawTwoColumns(visible, nowRel, distinctBosses[0], distinctBosses[1], scale, width);
        }
        else
        {
            DrawSingleColumn(visible, nowRel, scale, width);
        }
    }

    /// <summary>リスト先頭に現在フェーズ名（2 体フェーズなら「2 体」注記）を 1 行で示す。</summary>
    /// <remarks>フェーズ未注釈かつ 1 体のときは描画しない（フェーズ非対応コンテンツは従来どおり）。</remarks>
    private void DrawPhaseHeader(float scale, float width, bool twoBoss)
    {
        var phaseId = _phaseTracker?.CurrentPhaseId;
        if (string.IsNullOrEmpty(phaseId) && !twoBoss)
        {
            return;
        }
        var label = string.IsNullOrEmpty(phaseId) ? string.Empty : $"フェーズ {phaseId}";
        if (twoBoss)
        {
            label = string.IsNullOrEmpty(label) ? "2 体フェーズ" : $"{label}（2 体）";
        }

        var draw = ImGui.GetWindowDrawList();
        var headerH = 18f * scale;
        var start = ImGui.GetCursorScreenPos();
        var end = new Vector2(start.X + width, start.Y + headerH);
        draw.AddRectFilled(start, end, 0xE0202020u, 4f);
        draw.AddText(new Vector2(start.X + 8f * scale, start.Y + 2f * scale), 0xFFF0D060u, Truncate(label, 30));
        ImGui.SetCursorScreenPos(new Vector2(start.X, end.Y + 3f * scale));
    }

    /// <summary>1 体（従来）モード：source 別グループ見出し + 縮むバーを縦に積む。</summary>
    private void DrawSingleColumn(List<UpcomingItem> visible, double nowRel, float scale, float width)
    {
        var draw = ImGui.GetWindowDrawList();
        const int MaxRows = 8;
        var rowH = 22f * scale;
        var rowSpacing = 4f * scale;
        var headerH = 18f * scale;

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
                var headerEnd = new Vector2(headerStart.X + width, headerStart.Y + headerH);
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

                var rowStart = ImGui.GetCursorScreenPos();
                DrawBarRow(scale, rowStart, width, rowH, it, nowRel, $"##upcoming-row-{rowIndex}", null, 22);
                ImGui.SetCursorScreenPos(new Vector2(rowStart.X, rowStart.Y + rowH + rowSpacing));
                rowsDrawn++;
                rowIndex++;
            }
        }
    }

    /// <summary>2 体モード：左右 2 カラムに各ボスの予定を時系列で分離表示する。</summary>
    private void DrawTwoColumns(
        List<UpcomingItem> visible, double nowRel, string leftBoss, string rightBoss, float scale, float width)
    {
        var rowH = 22f * scale;
        var rowSpacing = 4f * scale;
        var headerH = 18f * scale;
        var colGap = 6f * scale;
        var colWidth = (width - colGap) / 2f;
        const int MaxRowsPerCol = 7;

        var leftColor = SourceColor(leftBoss);
        var rightColor = SourceColor(rightBoss);
        if (leftColor == rightColor)
        {
            rightColor = SourceColorAlt(rightBoss);
        }

        var leftItems = new List<UpcomingItem>();
        var rightItems = new List<UpcomingItem>();
        foreach (var it in visible)
        {
            var group = UpcomingTimelinePolicy.SourceGroupName(it.Source, it.Label);
            if (string.Equals(group, rightBoss, StringComparison.OrdinalIgnoreCase))
            {
                rightItems.Add(it);
            }
            else
            {
                // 左ボス + 共通 + その他ソースは左カラムへ寄せる（取りこぼし防止）。
                leftItems.Add(it);
            }
        }

        var top = ImGui.GetCursorScreenPos();
        DrawColumn(new Vector2(top.X, top.Y), colWidth, rowH, rowSpacing, headerH, scale,
            leftBoss, leftColor, leftItems, nowRel, MaxRowsPerCol, 0);
        DrawColumn(new Vector2(top.X + colWidth + colGap, top.Y), colWidth, rowH, rowSpacing, headerH, scale,
            rightBoss, rightColor, rightItems, nowRel, MaxRowsPerCol, 1);
    }

    private void DrawColumn(
        Vector2 origin, float colWidth, float rowH, float rowSpacing, float headerH, float scale,
        string boss, uint bossColor, List<UpcomingItem> items, double nowRel, int maxRows, int colIndex)
    {
        var draw = ImGui.GetWindowDrawList();
        var headerEnd = new Vector2(origin.X + colWidth, origin.Y + headerH);
        draw.AddRectFilled(origin, headerEnd, 0xCC101010u, 4f);
        // ボス色のタグ帯（左端）＋ボス名。
        draw.AddRectFilled(origin, new Vector2(origin.X + 4f * scale, headerEnd.Y), bossColor, 0f);
        draw.AddText(new Vector2(origin.X + 10f * scale, origin.Y + 2f * scale), 0xFFE6E6E6u, Truncate(boss, 12));

        // ラベル最大文字数はカラム幅から動的に決める。固定 11 だと狭い既定幅(≈147px)で日本語技名が
        // 隣カラムへはみ出し、ユーザーがウィンドウを広げても表示が増えない。時刻表示分(≈58px)を引いて
        // 1 文字 ≈ 12px で見積もる（最低 4 文字は確保）。
        var maxLabelChars = Math.Max(4, (int)((colWidth - 58f * scale) / (12f * scale)));

        var y = headerEnd.Y + 2f * scale;
        var rows = 0;
        foreach (var it in items)
        {
            if (rows >= maxRows)
            {
                break;
            }
            // このカラムのボス本人の技はボス色帯、共通ノート等はカラム色と紛れないよう中立グレー帯にする。
            var grp = UpcomingTimelinePolicy.SourceGroupName(it.Source, it.Label);
            var band = string.Equals(grp, boss, StringComparison.OrdinalIgnoreCase) ? bossColor : CommonBandColor;
            DrawBarRow(scale, new Vector2(origin.X, y), colWidth, rowH, it, nowRel,
                $"##upcoming-c{colIndex}-r{rows}", band, maxLabelChars);
            y += rowH + rowSpacing;
            rows++;
        }
    }

    /// <summary>2 カラム時、ボス本人でない行（共通ノート等）の色帯に使う中立グレー。</summary>
    private const uint CommonBandColor = 0xFF808080u;

    /// <summary>1 行（縮むバー）を指定スクリーン矩形に描く。両モード共通。</summary>
    /// <param name="sourceBand">指定があれば左端に発生源色帯を引く（2 体モードでカラム色と束ねる）。</param>
    private void DrawBarRow(
        float scale, Vector2 rowStart, float rowWidth, float rowH,
        UpcomingItem it, double nowRel, string buttonId, uint? sourceBand, int maxLabelChars)
    {
        const float BarWindowSec = 30f;       // バー全長 = 30 秒先
        // 文字色は常に濃いグレー（黒）。背景白・バー色付きでも一貫して読める。
        const uint TextColor = 0xFF1A1A1Au;
        const uint LaneBgColor = 0xFFF0F0F0u;
        const uint LaneBorderColor = 0xFFA0A0A0u;

        var draw = ImGui.GetWindowDrawList();
        var remaining = (float)(it.Time - nowRel);
        // バー長：残時間 / 窓長。残時間多いほどバー長い（cactbot 流）。過去は短い赤バーで余韻。
        var fillRatio = remaining > 0
            ? Math.Clamp(remaining / BarWindowSec, 0.02f, 1f)
            : 0.04f;
        var barColor = BarColorForRemaining(remaining);

        var rowEnd = new Vector2(rowStart.X + rowWidth, rowStart.Y + rowH);
        var barEnd = new Vector2(rowStart.X + rowWidth * fillRatio, rowEnd.Y);

        draw.AddRectFilled(rowStart, rowEnd, LaneBgColor, 4f);
        draw.AddRectFilled(rowStart, barEnd, barColor, 4f);
        draw.AddRect(rowStart, rowEnd, LaneBorderColor, 4f, ImDrawFlags.None, 1f);
        // 直近 5 秒以内は赤枠で強調（白枠だと白背景に紛れるため）。
        if (remaining <= 5f && remaining >= -1f)
        {
            draw.AddRect(rowStart, rowEnd, 0xFF3030F0u, 4f, ImDrawFlags.None, 2f);
        }

        var textLeft = rowStart.X + 8f * scale;
        if (sourceBand is { } band)
        {
            draw.AddRectFilled(rowStart, new Vector2(rowStart.X + 3f * scale, rowEnd.Y), band, 0f);
            textLeft = rowStart.X + 7f * scale;
        }

        var timeText = remaining < 0 ? $"+{(-remaining):F1}s" : $"{remaining:F1}s";
        ImGui.SetWindowFontScale(1.05f);
        var timeSize = ImGui.CalcTextSize(timeText);
        draw.AddText(new Vector2(textLeft, rowStart.Y + (rowH - timeSize.Y) * 0.5f), TextColor, timeText);
        var rowLabel = UpcomingTimelinePolicy.FormatRowLabel(it.Label, it.Source);
        var labelText = Truncate(rowLabel, maxLabelChars);
        var labelSize = ImGui.CalcTextSize(labelText);
        // 技名X：色帯なし(1体)時は rowStart+60f で従来と一致。色帯あり(2体)時は 7+52=59f。
        draw.AddText(new Vector2(textLeft + 52f * scale, rowStart.Y + (rowH - labelSize.Y) * 0.5f), TextColor, labelText);
        ImGui.SetWindowFontScale(1f);

        // 行全体に当たり判定 → ホバー時に詳細 tooltip
        ImGui.SetCursorScreenPos(rowStart);
        ImGui.InvisibleButton(buttonId, new Vector2(rowWidth, rowH));
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
    }

    // 色階層：cactbot の info/soon/alarm + 過ぎた赤。白背景にコントラストする色。
    private static uint BarColorForRemaining(float remaining)
    {
        if (remaining <= 1f) return 0xFF3030F0u;   // 真赤
        if (remaining <= 5f) return 0xFF3FA0F8u;    // 橙
        if (remaining <= 15f) return 0xFF50C8E8u;   // 黄
        return 0xFFD8C8B0u;                          // 薄水
    }

    private static uint Rgb(byte r, byte g, byte b)
        => 0xFF000000u | ((uint)b << 16) | ((uint)g << 8) | r;

    // 発生源（ボス）名→安定色。2 体フェーズの左右カラム識別と行色帯に使う。
    private static readonly uint[] SourcePalette =
    {
        Rgb(66, 133, 244),   // 青
        Rgb(244, 140, 66),   // 橙
        Rgb(76, 175, 80),    // 緑
        Rgb(171, 99, 200),   // 紫
    };

    private static uint SourceColorIndex(string source, uint offset)
    {
        var hash = 2166136261u;
        foreach (var ch in source)
        {
            hash = (hash ^ ch) * 16777619u;
        }
        return SourcePalette[(hash + offset) % (uint)SourcePalette.Length];
    }

    private static uint SourceColor(string source) => SourceColorIndex(source, 0);

    private static uint SourceColorAlt(string source) => SourceColorIndex(source, 1);

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

        var file = _store.GetByZone(_currentZone);
        // 分岐確定状態を反映：rejected branch / pending（未確定）branch の攻撃はタイムラインから
        // 除外し「共通だけ見せる」既定ポリシー。録画予測・戦略ノートの両方に同じ branchCheck を適用する。
        Func<string?, bool>? branchCheck = _branchObserver is { } bo
            ? bo.IsActiveOrCommon
            : null;
        // 現在フェーズ絞り込み：過去フェーズの mechanic はタイムラインから除外する
        // （phase 未注釈のコンテンツでは常に true = 全表示で後方互換）。
        Func<string?, bool>? phaseCheck = _phaseTracker is { } pt
            ? pt.IsPhaseActive
            : null;

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
            if (file is not null && branchCheck is not null)
            {
                displayPredictions = UpcomingTimelinePolicy.FilterBranchRejectedPredictions(
                    displayPredictions, file, branchCheck);
            }
            // 過去フェーズの予測技がバーに残留しないよう、攻略ノートと同じく phase 絞り込みを適用する
            // （録画予測には phase 注釈が無いため mechanic 逆引きで解決）。phase 未注釈なら全表示で後方互換。
            if (file is not null && phaseCheck is not null)
            {
                displayPredictions = UpcomingTimelinePolicy.FilterPastPhasePredictions(
                    displayPredictions, file, phaseCheck);
            }
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

        if (file is not null)
        {
            var strategyNotes = StrategyPlanResolver.BuildTimelineNotes(file, branchCheck, phaseCheck);
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
