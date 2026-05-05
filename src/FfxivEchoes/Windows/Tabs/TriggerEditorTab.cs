using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using FfxivEchoes.Recording;
using FfxivEchoes.Triggers;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Windows.Tabs;

/// <summary>
/// 選択中ゾーンのトリガー編集 + 観測イベント集計ビュー（SPEC.md §7.2）。
/// </summary>
public sealed class TriggerEditorTab : ITab
{
    public string Title => "トリガー編集";
    public string Id => "trigger-editor";

    private readonly TriggerStore _triggerStore;
    private readonly RecordingScanner _recordingScanner;
    private readonly TabContext _tabContext;
    private readonly Events.IEventBus? _eventBus;

    private string? _editingTriggerId;
    private TriggerFile? _workingCopy;
    private string _workingZone = string.Empty;
    private bool _dirty;
    private readonly TimelineRenderer _timelineRenderer = new();
    private bool _aggregateAsTimeline = true;

    public TriggerEditorTab(TriggerStore triggerStore, RecordingScanner scanner, TabContext context,
        Events.IEventBus? eventBus = null)
    {
        _triggerStore = triggerStore;
        _recordingScanner = scanner;
        _tabContext = context;
        _eventBus = eventBus;
    }

    public void Draw()
    {
        var zone = _tabContext.SelectedZone;
        if (string.IsNullOrEmpty(zone))
        {
            ImGui.TextWrapped("コンテンツ一覧から「編集」を押してゾーンを選択してください。");
            return;
        }

        // ゾーンが切り替わったら作業コピーを再構築
        if (_workingZone != zone)
        {
            LoadWorkingCopy(zone);
        }
        if (_workingCopy is null)
        {
            ImGui.TextWrapped($"ゾーン \"{zone}\" のトリガー定義は未作成です。下のボタンで新規作成できます。");
            if (ImGui.Button("新規作成"))
            {
                _workingCopy = new TriggerFile { Zone = zone };
                _workingZone = zone;
                _dirty = true;
            }
            return;
        }

        DrawHeader(zone);
        ImGui.Separator();

        if (ImGui.BeginTabBar("##trigger-editor-subtabs"))
        {
            if (ImGui.BeginTabItem("トリガー一覧"))
            {
                DrawTriggerListPanel();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("集計（観測イベント）"))
            {
                DrawAggregatePanel(zone);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("ファイル設定"))
            {
                DrawFileSettingsPanel();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("バックアップ"))
            {
                DrawBackupsPanel(zone);
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    private void LoadWorkingCopy(string zone)
    {
        var existing = _triggerStore.GetByZone(zone);
        _workingCopy = existing is null
            ? null
            : Clone(existing);
        _workingZone = zone;
        _editingTriggerId = null;
        _dirty = false;
    }

    private void DrawHeader(string zone)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"ゾーン: ");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(1f, 0.85f, 0.4f, 1f), zone);
        ImGui.SameLine();
        ImGui.TextDisabled($"  /  バージョン {_workingCopy!.Version}  /  トリガー {_workingCopy.Triggers.Count} 件");

        ImGui.SameLine(0, 32f * ImGuiHelpers.GlobalScale);
        var saveLabel = _dirty ? "保存（未保存の変更あり）" : "保存";
        if (ImGui.Button(saveLabel))
        {
            try
            {
                _triggerStore.SaveZone(zone, _workingCopy);
                _dirty = false;
            }
            catch (Exception ex)
            {
                ImGui.OpenPopup("save-error");
                ImGui.SetNextWindowSize(new Vector2(400, 0));
                _saveError = ex.Message;
            }
        }

        if (ImGui.BeginPopupModal("save-error", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped(_saveError ?? "保存に失敗しました。");
            if (ImGui.Button("OK"))
            {
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("変更を破棄"))
        {
            LoadWorkingCopy(zone);
        }
    }

    private string? _saveError;

    private void DrawTriggerListPanel()
    {
        if (_workingCopy is null)
        {
            return;
        }

        if (ImGui.Button("新規トリガー"))
        {
            var newTrigger = new TriggerDefinition
            {
                Id = $"new_trigger_{_workingCopy.Triggers.Count + 1}",
                Type = "cast_start",
                Match = new MatchCondition(),
                Actions = new List<ActionDefinition> { new() { Type = "tts", Text = "" } },
            };
            _workingCopy.Triggers.Add(newTrigger);
            _editingTriggerId = newTrigger.Id;
            _dirty = true;
        }
        ImGui.Spacing();

        if (_workingCopy.Triggers.Count == 0)
        {
            ImGui.TextDisabled("トリガーがありません。「新規トリガー」で追加してください。");
            return;
        }

        // 二段組：左にリスト、右に編集パネル
        var available = ImGui.GetContentRegionAvail();
        var listWidth = MathF.Min(280f * ImGuiHelpers.GlobalScale, available.X * 0.45f);

        if (ImGui.BeginChild("##trigger-list", new Vector2(listWidth, 0), true))
        {
            for (int i = 0; i < _workingCopy.Triggers.Count; i++)
            {
                var t = _workingCopy.Triggers[i];
                var label = $"{(t.Enabled ? "● " : "○ ")}{t.Id}##list-{i}";
                if (ImGui.Selectable(label, _editingTriggerId == t.Id))
                {
                    _editingTriggerId = t.Id;
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"type: {t.Type}\nactions: {t.Actions.Count}\n{(t.Name ?? "(no name)")}");
                }
            }
        }
        ImGui.EndChild();

        ImGui.SameLine();

        if (ImGui.BeginChild("##trigger-edit", new Vector2(0, 0), true))
        {
            DrawTriggerEditor();
        }
        ImGui.EndChild();
    }

    private void DrawTriggerEditor()
    {
        if (_workingCopy is null)
        {
            return;
        }
        if (_editingTriggerId is null)
        {
            ImGui.TextDisabled("左でトリガーを選択してください。");
            return;
        }
        var trigger = _workingCopy.Triggers.FirstOrDefault(t => t.Id == _editingTriggerId);
        if (trigger is null)
        {
            ImGui.TextDisabled("選択されたトリガーが見つかりません。");
            return;
        }

        // 基本情報
        var enabled = trigger.Enabled;
        if (ImGui.Checkbox("有効", ref enabled))
        {
            trigger.Enabled = enabled;
            _dirty = true;
        }

        ImGui.Spacing();
        var id = trigger.Id;
        ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("ID##trigger-id"u8, ref id, 64))
        {
            trigger.Id = id;
            _editingTriggerId = id;
            _dirty = true;
        }

        var name = trigger.Name ?? string.Empty;
        ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("表示名##trigger-name"u8, ref name, 128))
        {
            trigger.Name = string.IsNullOrEmpty(name) ? null : name;
            _dirty = true;
        }

        var typeIndex = Array.IndexOf(EventTypes, trigger.Type);
        if (typeIndex < 0)
        {
            typeIndex = 0;
        }
        ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
        if (ImGui.Combo("タイプ##trigger-type", ref typeIndex, EventTypes, EventTypes.Length))
        {
            trigger.Type = EventTypes[typeIndex];
            _dirty = true;
        }

        var cooldown = (float)(trigger.Cooldown ?? 0);
        ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputFloat("クールダウン (s)##trigger-cd", ref cooldown))
        {
            trigger.Cooldown = cooldown <= 0 ? null : cooldown;
            _dirty = true;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("マッチ条件");
        DrawMatchEditor(trigger);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("アクション");
        DrawActionsEditor(trigger);

        ImGui.Spacing();
        ImGui.Separator();
        if (ImGui.Button("このトリガーを削除"))
        {
            _workingCopy.Triggers.Remove(trigger);
            _editingTriggerId = null;
            _dirty = true;
        }
        ImGui.SameLine();
        if (ImGui.Button("テスト発動"u8))
        {
            FirePreview(trigger);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("このトリガーを今すぐ発火させて、アクション動作を確認します。");
        }
    }

    private void FirePreview(TriggerDefinition trigger)
    {
        if (_eventBus is null)
        {
            return;
        }
        // 偽の SourceEvent として CombatStartedEvent を使う
        var fakeSource = new Events.CombatStartedEvent(System.DateTimeOffset.UtcNow);
        _eventBus.Publish(new Events.TriggerFiredEvent(
            Timestamp: System.DateTimeOffset.UtcNow,
            Zone: _workingZone,
            TriggerId: trigger.Id,
            TriggerName: trigger.Name,
            Actions: trigger.Actions,
            SourceEvent: fakeSource));
    }

    private void DrawMatchEditor(TriggerDefinition trigger)
    {
        trigger.Match ??= new MatchCondition();
        var m = trigger.Match;

        var castId = m.CastId ?? string.Empty;
        ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("cast_id (例: 0x9D32)##match-cast-id", ref castId, 32))
        {
            m.CastId = string.IsNullOrEmpty(castId) ? null : castId;
            _dirty = true;
        }

        var castName = m.CastName ?? string.Empty;
        ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("cast_name##match-cast-name", ref castName, 128))
        {
            m.CastName = string.IsNullOrEmpty(castName) ? null : castName;
            _dirty = true;
        }

        var statusId = (int)(m.StatusId ?? 0);
        ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("status_id##match-status-id", ref statusId))
        {
            m.StatusId = statusId <= 0 ? null : (uint)statusId;
            _dirty = true;
        }

        var source = m.Source ?? string.Empty;
        ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("source##match-source", ref source, 64))
        {
            m.Source = string.IsNullOrEmpty(source) ? null : source;
            _dirty = true;
        }

        // target は単純に文字列で（M8 では配列対応はしない）
        var target = m.Target?.Values is { Count: > 0 } v ? v[0] : string.Empty;
        ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("target (self/tank/healer/...)##match-target", ref target, 32))
        {
            m.Target = string.IsNullOrEmpty(target)
                ? null
                : new TargetSpec(new List<string> { target });
            _dirty = true;
        }
    }

    private void DrawActionsEditor(TriggerDefinition trigger)
    {
        for (int i = 0; i < trigger.Actions.Count; i++)
        {
            var action = trigger.Actions[i];
            ImGui.PushID(i);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted($"#{i + 1}");
            ImGui.SameLine();

            var typeIndex = Array.IndexOf(ActionTypes, action.Type);
            if (typeIndex < 0)
            {
                typeIndex = 0;
            }
            ImGui.SetNextItemWidth(150f * ImGuiHelpers.GlobalScale);
            if (ImGui.Combo("##action-type", ref typeIndex, ActionTypes, ActionTypes.Length))
            {
                action.Type = ActionTypes[typeIndex];
                _dirty = true;
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("↑") && i > 0)
            {
                (trigger.Actions[i - 1], trigger.Actions[i]) = (trigger.Actions[i], trigger.Actions[i - 1]);
                _dirty = true;
                ImGui.PopID();
                continue;
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("↓") && i < trigger.Actions.Count - 1)
            {
                (trigger.Actions[i + 1], trigger.Actions[i]) = (trigger.Actions[i], trigger.Actions[i + 1]);
                _dirty = true;
                ImGui.PopID();
                continue;
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("削除"))
            {
                trigger.Actions.RemoveAt(i);
                _dirty = true;
                ImGui.PopID();
                continue;
            }

            // type に応じた最低限のフィールド
            ImGui.Indent(20f);
            switch (action.Type)
            {
                case "tts":
                case "chat_echo":
                case "overlay_text":
                {
                    var text = action.Text ?? string.Empty;
                    ImGui.SetNextItemWidth(380f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputText("text##action-text", ref text, 256))
                    {
                        action.Text = text;
                        _dirty = true;
                    }
                    if (action.Type == "overlay_text")
                    {
                        var dur = (float)(action.Duration ?? 5.0);
                        ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
                        if (ImGui.InputFloat("duration (s)##action-duration", ref dur))
                        {
                            action.Duration = dur <= 0 ? null : dur;
                            _dirty = true;
                        }
                    }
                    break;
                }
                case "wav":
                {
                    var file = action.File ?? string.Empty;
                    ImGui.SetNextItemWidth(380f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputText("file##action-file", ref file, 256))
                    {
                        action.File = file;
                        _dirty = true;
                    }
                    break;
                }
                case "timer_bar":
                {
                    var label = action.Label ?? string.Empty;
                    ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputText("label##action-label", ref label, 128))
                    {
                        action.Label = label;
                        _dirty = true;
                    }
                    var dur = (float)(action.Duration ?? 0.0);
                    ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputFloat("duration (s)##action-duration", ref dur))
                    {
                        action.Duration = dur <= 0 ? null : dur;
                        _dirty = true;
                    }
                    break;
                }
                default:
                    ImGui.TextDisabled($"({action.Type} は M8 範囲外。JSON 直接編集を推奨)");
                    break;
            }

            // 共通：delay
            var delay = (float)action.Delay;
            ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
            if (ImGui.InputFloat("delay (s)##action-delay", ref delay))
            {
                action.Delay = MathF.Max(0, delay);
                _dirty = true;
            }
            ImGui.Unindent(20f);
            ImGui.Spacing();
            ImGui.PopID();
        }

        if (ImGui.Button("アクション追加"))
        {
            trigger.Actions.Add(new ActionDefinition { Type = "tts" });
            _dirty = true;
        }
    }

    private void DrawAggregatePanel(string zone)
    {
        var agg = _recordingScanner.Aggregate(zone);
        ImGui.TextDisabled($"録画ファイル {agg.RecordingFileCount} / 戦闘 {agg.BattleCount} / 総イベント {agg.TotalEventCount}");
        ImGui.SameLine(0, 24f);
        ImGui.Checkbox("タイムライン表示", ref _aggregateAsTimeline);
        ImGui.Spacing();

        if (agg.Events.Count == 0)
        {
            ImGui.TextWrapped("録画データから集計できるイベントがありません。/echoes record on で録画してください。");
            return;
        }

        if (_aggregateAsTimeline)
        {
            _timelineRenderer.Draw(agg);
            if (_timelineRenderer.SelectedEvent is { } selected)
            {
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.TextUnformatted($"選択: {selected.Type} / {selected.Id ?? selected.Name ?? "—"}");
                ImGui.SameLine();
                if (ImGui.Button("このイベントからトリガー作成"))
                {
                    CreateTriggerFromKey(selected);
                }
            }
            return;
        }

        if (ImGui.BeginTable("##aggregate-table", 6,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp |
            ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY))
        {
            ImGui.TableSetupColumn("種別", ImGuiTableColumnFlags.WidthFixed, 100f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthFixed, 100f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("名前", ImGuiTableColumnFlags.WidthStretch, 2.0f);
            ImGui.TableSetupColumn("発動者/対象", ImGuiTableColumnFlags.WidthStretch, 1.5f);
            ImGui.TableSetupColumn("観測回数", ImGuiTableColumnFlags.WidthFixed, 80f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("初回時刻", ImGuiTableColumnFlags.WidthFixed, 90f * ImGuiHelpers.GlobalScale);
            ImGui.TableHeadersRow();

            foreach (var ev in agg.Events)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(ev.Key.Type);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(ev.Key.Id ?? "—");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(ev.Key.Name ?? "—");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(ev.Key.Source ?? ev.Key.Target ?? "—");
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{ev.Count}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{ev.FirstSeenSeconds:0.0}s");
            }

            ImGui.EndTable();
        }
    }

    private void CreateTriggerFromKey(EventKey key)
    {
        if (_workingCopy is null)
        {
            return;
        }
        // 既存トリガーが同じキーを持っているかは厳密には判定しないが、
        // 重複 ID 防止のためサフィックスを付ける
        var baseId = SuggestId(key);
        var id = baseId;
        var n = 1;
        while (_workingCopy.Triggers.Any(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            n++;
            id = $"{baseId}_{n}";
        }

        var trigger = new TriggerDefinition
        {
            Id = id,
            Type = key.Type,
            Match = BuildMatchFromKey(key),
            Actions = new List<ActionDefinition>
            {
                new() { Type = "tts", Text = key.Name ?? key.Id ?? key.Type },
            },
        };
        _workingCopy.Triggers.Add(trigger);
        _editingTriggerId = trigger.Id;
        _dirty = true;
        _tabContext.PendingFocusTab = null; // 集計タブに留まる
    }

    private static string SuggestId(EventKey key) => key.Type switch
    {
        "cast_start" or "cast_complete" or "cast_cancel" => $"cast_{key.Id ?? key.Name ?? "x"}".ToLowerInvariant().Replace(" ", "_"),
        "status_gain" or "status_lose" or "status_update" => $"status_{key.Id ?? key.Name ?? "x"}".ToLowerInvariant().Replace(" ", "_"),
        "hp_change" => $"hp_{key.Source ?? "actor"}",
        _ => key.Type,
    };

    private static MatchCondition BuildMatchFromKey(EventKey key)
    {
        var m = new MatchCondition();
        switch (key.Type)
        {
            case "cast_start":
            case "cast_complete":
            case "cast_cancel":
                m.CastId = key.Id;
                m.CastName = key.Name;
                m.Source = key.Source;
                break;
            case "status_gain":
            case "status_lose":
            case "status_update":
                if (key.Id is not null && uint.TryParse(key.Id, out var statusId))
                {
                    m.StatusId = statusId;
                }
                m.StatusName = key.Name;
                if (key.Target is not null)
                {
                    m.Target = new TargetSpec(new List<string> { key.Target });
                }
                break;
            case "hp_change":
                m.Actor = key.Source;
                break;
        }
        return m;
    }

    private void DrawFileSettingsPanel()
    {
        if (_workingCopy is null)
        {
            return;
        }

        var enableTrig = _workingCopy.AutoSettings.EnableTriggers;
        if (ImGui.Checkbox("enable_triggers（突入時に自動でトリガー有効化）", ref enableTrig))
        {
            _workingCopy.AutoSettings.EnableTriggers = enableTrig;
            _dirty = true;
        }
        var autoRec = _workingCopy.AutoSettings.AutoRecord;
        if (ImGui.Checkbox("auto_record（突入時に自動で録画開始）", ref autoRec))
        {
            _workingCopy.AutoSettings.AutoRecord = autoRec;
            _dirty = true;
        }
        var showTl = _workingCopy.AutoSettings.ShowTimeline;
        if (ImGui.Checkbox("show_timeline（突入時にライブHUDを表示）", ref showTl))
        {
            _workingCopy.AutoSettings.ShowTimeline = showTl;
            _dirty = true;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled($"バージョン: {_workingCopy.Version}");
        var path = _triggerStore.GetFilePathForZone(_workingZone);
        if (path is not null)
        {
            ImGui.TextDisabled($"ファイル: {path}");
        }
    }

    private string? _pendingRestorePath;

    private void DrawBackupsPanel(string zone)
    {
        var backupManager = _triggerStore.BackupManager;
        if (backupManager is null)
        {
            ImGui.TextDisabled("バックアップ機構が利用できません。");
            return;
        }

        ImGui.TextWrapped("ゾーン定義の保存（上書き）時には、直前の状態が自動でバックアップされます。" +
            "古いバックアップは保持上限を超えると自動削除されます。");
        ImGui.Spacing();

        var backups = backupManager.List(zone);
        ImGui.TextDisabled($"{backups.Count} 件のバックアップ");
        ImGui.Spacing();

        if (backups.Count == 0)
        {
            ImGui.TextWrapped("まだバックアップがありません。一度「保存」を行うとここに表示されます。");
            return;
        }

        if (ImGui.BeginTable("##backups-table", 4,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp |
            ImGuiTableFlags.ScrollY))
        {
            ImGui.TableSetupColumn("作成日時", ImGuiTableColumnFlags.WidthStretch, 2.0f);
            ImGui.TableSetupColumn("サイズ", ImGuiTableColumnFlags.WidthFixed, 80f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("ファイル", ImGuiTableColumnFlags.WidthStretch, 3.0f);
            ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 130f * ImGuiHelpers.GlobalScale);
            ImGui.TableHeadersRow();

            foreach (var backup in backups)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(backup.CreatedAtLocal.ToString("yyyy-MM-dd HH:mm:ss"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{backup.Size:N0} B");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(System.IO.Path.GetFileName(backup.Path));
                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"復元##{backup.Path}"))
                {
                    _pendingRestorePath = backup.Path;
                    ImGui.OpenPopup("restore-confirm");
                }
            }
            ImGui.EndTable();
        }

        if (ImGui.BeginPopupModal("restore-confirm", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped("このバックアップで現在の定義を上書きしますか？");
            ImGui.TextDisabled(_pendingRestorePath ?? string.Empty);
            ImGui.Spacing();
            ImGui.TextWrapped("（上書き前の現状もバックアップが取られます）");
            ImGui.Spacing();
            if (ImGui.Button("復元する"))
            {
                if (_pendingRestorePath is { } path)
                {
                    _triggerStore.RestoreFromBackup(zone, path);
                    LoadWorkingCopy(zone);
                }
                _pendingRestorePath = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("キャンセル"))
            {
                _pendingRestorePath = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
    }

    private static TriggerFile Clone(TriggerFile src)
    {
        // 編集中は元データを汚さないようディープコピー（System.Text.Json 経由で簡易に）
        var json = TriggerSerializer.Serialize(src);
        return System.Text.Json.JsonSerializer.Deserialize<TriggerFile>(json,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = false,
            }) ?? new TriggerFile();
    }

    private static readonly string[] EventTypes =
    {
        "cast_start", "cast_complete", "cast_cancel",
        "action_used",
        "status_gain", "status_lose", "status_update",
        "hp_change", "zone_change",
        "combat_start", "combat_end",
        "object_appear", "object_disappear", "timeline_elapsed",
    };

    private static readonly string[] ActionTypes =
    {
        "tts", "wav", "overlay_text", "overlay_corner_text", "timer_bar",
        "chat_echo", "direction_call", "screen_arrow", "field_marker",
        "proximity_feedback", "set_variable", "chain_trigger",
    };
}
