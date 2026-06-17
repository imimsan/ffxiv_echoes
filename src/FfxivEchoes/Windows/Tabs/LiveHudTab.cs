using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using FfxivEchoes.Capture;
using FfxivEchoes.Events;

namespace FfxivEchoes.Windows.Tabs;

/// <summary>
/// 直近のキャプチャ済みイベントをリアルタイム表示するデバッグ／観察タブ。
/// プラグインが実際にゲームから何を拾っているかを目で確認するための画面。
/// </summary>
public sealed class LiveHudTab : ITab, IDisposable
{
    public string Title => "ライブイベント";
    public string Id => "live-hud";

    private const int MaxEntries = 300;

    private readonly CombatClock _clock;
    private readonly IDisposable _sub;

    private readonly object _gate = new();
    private readonly LinkedList<EventEntry> _entries = new();

    // フィルタ状態
    private bool _showCast = true;
    private bool _showAction = true;
    private bool _showStatus = false;
    private bool _showHp = false;
    private bool _showObject = false;
    private bool _showCombat = true;
    private bool _showZone = true;
    private bool _showTrigger = true;
    private bool _autoScroll = true;
    private bool _paused = false;

    public LiveHudTab(IEventBus bus, CombatClock clock)
    {
        _clock = clock;
        _sub = bus.SubscribeAll(OnEvent);
    }

    public void Dispose() => _sub.Dispose();

    private void OnEvent(IGameEvent ev)
    {
        if (_paused) return;

        var rel = _clock.RelativeSecondsAt(ev.Timestamp);
        var (kind, text, color) = Format(ev);
        var entry = new EventEntry(ev.Timestamp, rel, kind, text, color);

        lock (_gate)
        {
            _entries.AddLast(entry);
            while (_entries.Count > MaxEntries)
            {
                _entries.RemoveFirst();
            }
        }
    }

    public void Draw()
    {
        ImGui.TextWrapped("プラグインがゲームから拾ったイベントをリアルタイム表示します。" +
                          "新規トリガー作成時や、想定の cast_id を確認したい時に使う。");
        ImGui.Spacing();

        if (ImGui.Button(_paused ? "再開" : "一時停止"))
        {
            _paused = !_paused;
        }
        ImGui.SameLine();
        if (ImGui.Button("クリア"))
        {
            lock (_gate) { _entries.Clear(); }
        }
        ImGui.SameLine();
        ImGui.Checkbox("自動スクロール", ref _autoScroll);

        ImGui.Spacing();
        ImGui.TextDisabled("フィルタ：");
        ImGui.SameLine(); ImGui.Checkbox("Cast", ref _showCast);
        ImGui.SameLine(); ImGui.Checkbox("Action/AA", ref _showAction);
        ImGui.SameLine(); ImGui.Checkbox("Status", ref _showStatus);
        ImGui.SameLine(); ImGui.Checkbox("HP", ref _showHp);
        ImGui.SameLine(); ImGui.Checkbox("Object", ref _showObject);
        ImGui.SameLine(); ImGui.Checkbox("Combat", ref _showCombat);
        ImGui.SameLine(); ImGui.Checkbox("Zone", ref _showZone);
        ImGui.SameLine(); ImGui.Checkbox("Trigger", ref _showTrigger);

        ImGui.Spacing();
        ImGui.Separator();

        EventEntry[] snapshot;
        lock (_gate)
        {
            snapshot = _entries.ToArray();
        }

        if (ImGui.BeginTable("##live-events", 3,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
            ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY,
            new Vector2(0, 360 * ImGuiHelpers.GlobalScale)))
        {
            ImGui.TableSetupColumn("時刻", ImGuiTableColumnFlags.WidthFixed, 80f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("種別", ImGuiTableColumnFlags.WidthFixed, 80f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("内容", ImGuiTableColumnFlags.WidthStretch, 5.0f);
            ImGui.TableHeadersRow();

            foreach (var e in snapshot)
            {
                if (!IsKindEnabled(e.Kind)) continue;

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var t = e.Rel is { } r
                    ? $"t={r:0.0}s"
                    : e.Timestamp.ToLocalTime().ToString("HH:mm:ss.f");
                ImGui.TextDisabled(t);

                ImGui.TableNextColumn();
                ImGui.TextColored(e.Color, e.Kind);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(e.Text);
            }

            if (_autoScroll && !_paused)
            {
                ImGui.SetScrollHereY(1.0f);
            }
            ImGui.EndTable();
        }

        ImGui.TextDisabled($"  最大 {MaxEntries} 件保持。古いものから捨てられます。" +
                           (_paused ? "  ⏸ 一時停止中" : ""));
    }

    private bool IsKindEnabled(string kind) => kind switch
    {
        "cast" => _showCast,
        "action" => _showAction,
        "status" => _showStatus,
        "hp" => _showHp,
        "object" => _showObject,
        "combat" => _showCombat,
        "zone" => _showZone,
        "trigger" => _showTrigger,
        _ => true,
    };

    private static (string Kind, string Text, Vector4 Color) Format(IGameEvent ev) => ev switch
    {
        CombatStartedEvent => ("combat", "戦闘開始", new Vector4(1f, 0.85f, 0.4f, 1f)),
        CombatEndedEvent x => ("combat", $"戦闘終了（{x.Reason}）", new Vector4(0.7f, 0.7f, 0.7f, 1f)),
        ZoneChangedEvent x => ("zone", $"ゾーン → {x.ZoneName} (#{x.TerritoryId})", new Vector4(0.6f, 0.9f, 1f, 1f)),
        CastStartedEvent x => ("cast", $"キャスト開始 [{x.CastActionId:X4}] {x.SourceName} → {x.CastActionName} ({x.CastTime:0.0}s)", new Vector4(0.6f, 0.85f, 1f, 1f)),
        CastCompletedEvent x => ("cast", $"キャスト完了 [{x.CastActionId:X4}] {x.SourceName} → {x.CastActionName}", new Vector4(0.5f, 0.7f, 0.9f, 1f)),
        CastCanceledEvent x => ("cast", $"キャスト中断 [{x.CastActionId:X4}] {x.SourceName} → {x.CastActionName}", new Vector4(0.5f, 0.6f, 0.7f, 1f)),
        ActionUsedEvent x => ("action", $"アクション{(x.IsAutoAttack ? "(AA)" : "")} [{x.ActionId:X4}] {x.SourceName} → {x.ActionName}", new Vector4(0.95f, 0.95f, 0.7f, 1f)),
        StatusGainedEvent x => ("status", $"Status+ [{x.StatusId}] {x.TargetName} ← {x.StatusName}", new Vector4(0.7f, 0.95f, 0.7f, 1f)),
        StatusLostEvent x => ("status", $"Status- [{x.StatusId}] {x.TargetName} ← {x.StatusName}", new Vector4(0.6f, 0.7f, 0.6f, 1f)),
        StatusUpdatedEvent x => ("status", $"Status~ [{x.StatusId}] stacks={x.Stacks} t={x.RemainingTime:0.0}s", new Vector4(0.6f, 0.85f, 0.6f, 1f)),
        HpChangedEvent x => ("hp", $"HP {x.ActorName} {x.HpPct:0.0}% ({x.CurrentHp:N0}/{x.MaxHp:N0})", new Vector4(1f, 0.7f, 0.7f, 1f)),
        ObjectAppearedEvent x => ("object", $"Object+ [{x.ObjectId:X8}] {x.ObjectName}", new Vector4(0.85f, 0.85f, 0.9f, 1f)),
        ObjectDisappearedEvent x => ("object", $"Object- [{x.ObjectId:X8}] {x.ObjectName}", new Vector4(0.65f, 0.65f, 0.7f, 1f)),
        TriggerFiredEvent x => ("trigger", $"⚡ {x.TriggerId}{(x.TriggerName is { } n ? $" ({n})" : "")}", new Vector4(1f, 0.85f, 0.3f, 1f)),
        _ => ("misc", $"({ev.GetType().Name})", new Vector4(0.7f, 0.7f, 0.7f, 1f)),
    };

    private sealed record EventEntry(
        DateTimeOffset Timestamp,
        double? Rel,
        string Kind,
        string Text,
        Vector4 Color);
}
