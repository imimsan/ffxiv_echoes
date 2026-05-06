using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
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
    private readonly TriggerAutoGenerator? _autoGenerator;

    private string? _editingTriggerId;
    private TriggerFile? _workingCopy;
    private string _workingZone = string.Empty;
    private bool _dirty;
    private readonly TimelineRenderer _timelineRenderer = new();
    private bool _aggregateAsTimeline = true;
    private bool _hideSelfEvents = true;
    private bool _hideStatusEvents = false;
    private int _attachNoteIndex = -1;
    private TriggerAutoGenerator.GenerationResult? _pendingAutoGen;
    private string? _attachStrategyProfileId;
    private string? _attachStrategyMechanicId;

    public TriggerEditorTab(TriggerStore triggerStore, RecordingScanner scanner, TabContext context,
        Events.IEventBus? eventBus = null, TriggerAutoGenerator? autoGenerator = null)
    {
        _triggerStore = triggerStore;
        _recordingScanner = scanner;
        _tabContext = context;
        _eventBus = eventBus;
        _autoGenerator = autoGenerator;
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
        if (_workingCopy is null && _tabContext.PendingCreateFromRecording)
        {
            _workingCopy = CreateStarterFile(zone, fromRecording: true);
            _workingZone = zone;
            _tabContext.PendingCreateFromRecording = false;
            _dirty = true;
        }
        if (_workingCopy is null)
        {
            ImGui.TextWrapped($"ゾーン \"{zone}\" のトリガー定義は未作成です。下のボタンで新規作成できます。");
            if (ImGui.Button("新規作成"))
            {
                _workingCopy = CreateStarterFile(zone, _tabContext.PendingCreateFromRecording);
                _workingZone = zone;
                _tabContext.PendingCreateFromRecording = false;
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
            if (ImGui.BeginTabItem("攻略登録"))
            {
                DrawStrategyPanel();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("ノート"))
            {
                DrawNotesPanel();
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

    private static TriggerFile CreateStarterFile(string zone, bool fromRecording)
    {
        var now = DateTimeOffset.UtcNow;
        var file = new TriggerFile
        {
            Zone = zone,
            ActiveStrategyProfileId = "default",
            Metadata = new TriggerFileMetadata
            {
                CreatedAt = now,
                LastModified = now,
                Notes = fromRecording
                    ? "Created from recordings. Keep recording pulls to improve the learned timeline."
                    : "Created manually.",
            },
        };

        file.AutoSettings.EnableTriggers = true;
        file.AutoSettings.AutoRecord = fromRecording;
        file.AutoSettings.ShowTimeline = true;
        file.AutoSettings.ShowPredictedCasts = true;
        file.AutoSettings.ShowAutoTelegraphs = fromRecording;
        file.AutoSettings.ShowAllEnemyCasts = false;
        file.AutoSettings.ShowAutoAttacks = false;
        file.AutoSettings.PredictAdvanceWarningSec = AutoSettings.DefaultPredictAdvanceWarningSec;
        file.StrategyProfiles.Add(CreateDefaultStrategyProfile("default", "Default party strategy"));
        return file;
    }

    private static StrategyProfile CreateDefaultStrategyProfile(string id, string name)
    {
        return new StrategyProfile
        {
            Id = id,
            Name = name,
            ArenaRadius = 20.0,
            SpreadPositions = CreateEightWaySpreadPositions(),
        };
    }

    private static List<StrategyPosition> CreateEightWaySpreadPositions()
    {
        return new List<StrategyPosition>
        {
            new() { Slot = "MT", Label = "MT", Role = "tank", X = 0, Z = -14, Color = "#60A5FA" },
            new() { Slot = "ST", Label = "ST", Role = "tank", X = 0, Z = 14, Color = "#60A5FA" },
            new() { Slot = "H1", Label = "H1", Role = "healer", X = -14, Z = 0, Color = "#34D399" },
            new() { Slot = "H2", Label = "H2", Role = "healer", X = 14, Z = 0, Color = "#34D399" },
            new() { Slot = "D1", Label = "D1", Role = "dps", X = -10, Z = -10, Color = "#F87171" },
            new() { Slot = "D2", Label = "D2", Role = "dps", X = 10, Z = -10, Color = "#F87171" },
            new() { Slot = "D3", Label = "D3", Role = "dps", X = -10, Z = 10, Color = "#FBBF24" },
            new() { Slot = "D4", Label = "D4", Role = "dps", X = 10, Z = 10, Color = "#FBBF24" },
        };
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
        ImGui.SameLine(0, 24f * ImGuiHelpers.GlobalScale);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.55f, 0.3f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.25f, 0.7f, 0.4f, 1f));
        if (ImGui.Button("✨ 録画から自動生成"))
        {
            BeginAutoGeneration();
        }
        ImGui.PopStyleColor(2);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("録画されたボスのキャストから、Lumina の AoE データを参照して\n" +
                             "「TTS + 視覚通知」を含むトリガーを一括自動生成します。\n" +
                             "既に存在する cast_id はスキップ。生成後に個別編集も可能。");
        }

        DrawAutoGenPreviewPopup();
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
        var eventTypeLabels = Localization.LocalizeAll(EventTypes, Localization.EventType);
        ImGui.SetNextItemWidth(320f * ImGuiHelpers.GlobalScale);
        if (ImGui.Combo("発火タイミング##trigger-type", ref typeIndex, eventTypeLabels, eventTypeLabels.Length))
        {
            trigger.Type = EventTypes[typeIndex];
            _dirty = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("どのゲームイベントでこのトリガーを発動するか");

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
            var actionTypeLabels = Localization.LocalizeAll(ActionTypes, Localization.ActionType);
            ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
            if (ImGui.Combo("##action-type", ref typeIndex, actionTypeLabels, actionTypeLabels.Length))
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
                case "arena_view":
                {
                    // gimmick タイプ
                    var gimmick = action.Gimmick ?? "outer_ring";
                    var gIdx = Array.IndexOf(ArenaViewGimmicks, gimmick);
                    if (gIdx < 0) gIdx = 0;
                    var gimmickLabels = Localization.LocalizeAll(ArenaViewGimmicks, Localization.Gimmick);
                    ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
                    if (ImGui.Combo("ギミック種類##action-gimmick", ref gIdx, gimmickLabels, gimmickLabels.Length))
                    {
                        action.Gimmick = ArenaViewGimmicks[gIdx];
                        _dirty = true;
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(GimmickTooltip(action.Gimmick ?? "outer_ring"));
                    // callout
                    var callout = action.Callout ?? string.Empty;
                    ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputText("読み上げ・表示テキスト##action-callout", ref callout, 128))
                    {
                        action.Callout = callout;
                        _dirty = true;
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("ミニマップ下部に表示されるテキスト（例：「中央安置」）");
                    // duration
                    var dur = (float)(action.Duration ?? 5.0);
                    ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputFloat("表示秒数 (s)##action-duration", ref dur))
                    {
                        action.Duration = dur <= 0 ? null : dur;
                        _dirty = true;
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("ミニマップを表示する秒数。キャスト時間 + α が目安（例：5）");
                    // arena_radius
                    var ar = (float)(action.ArenaRadius ?? 20.0);
                    ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputFloat("アリーナ半径 (m)##action-ar", ref ar))
                    {
                        action.ArenaRadius = ar <= 0 ? null : ar;
                        _dirty = true;
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("プレイヤー位置を描画するためのアリーナ実半径（メートル）。極/絶はだいたい 18-25m");
                    // cone のときだけ direction + fan_deg
                    if (action.Gimmick == "cone")
                    {
                        var dir = action.Direction ?? "N";
                        var dIdx = Array.IndexOf(ArenaViewDirections, dir);
                        if (dIdx < 0) dIdx = 0;
                        var dirLabels = Localization.LocalizeAll(ArenaViewDirections, Localization.Direction);
                        ImGui.SetNextItemWidth(160f * ImGuiHelpers.GlobalScale);
                        if (ImGui.Combo("方向##action-dir", ref dIdx, dirLabels, dirLabels.Length))
                        {
                            action.Direction = ArenaViewDirections[dIdx];
                            _dirty = true;
                        }
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip("コーンが向く方位（北を上として）");
                        var fan = (float)(action.FanDeg ?? 90.0);
                        ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
                        if (ImGui.InputFloat("扇形の角度 (°)##action-fan", ref fan))
                        {
                            action.FanDeg = fan <= 0 ? null : fan;
                            _dirty = true;
                        }
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip("90 で 90 度の扇形（45 度ずつ左右に開く）");
                    }
                    DrawSafeZoneSubEditor(action);
                    break;
                }
                case "direction_call":
                {
                    var fmt = action.Format ?? "cardinal_jp";
                    var fIdx = Array.IndexOf(DirectionFormats, fmt);
                    if (fIdx < 0) fIdx = 0;
                    var fmtLabels = Localization.LocalizeAll(DirectionFormats, Localization.DirectionFormat);
                    ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
                    if (ImGui.Combo("読み上げ形式##action-fmt", ref fIdx, fmtLabels, fmtLabels.Length))
                    {
                        action.Format = DirectionFormats[fIdx];
                        _dirty = true;
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("方位の読み上げ方を選択。日本語方位なら「北」、時計なら「12時」など");

                    var ttsOn = action.Tts ?? true;
                    if (ImGui.Checkbox("TTS で読み上げ##dc-tts", ref ttsOn)) { action.Tts = ttsOn; _dirty = true; }
                    ImGui.SameLine();
                    var ovOn = action.Overlay ?? false;
                    if (ImGui.Checkbox("オーバーレイにも表示##dc-ov", ref ovOn)) { action.Overlay = ovOn; _dirty = true; }
                    DrawSafeZoneSubEditor(action);
                    break;
                }
                case "screen_arrow":
                {
                    var fromStr = action.From ?? "self";
                    ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputText("from##action-from", ref fromStr, 64)) { action.From = fromStr; _dirty = true; }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("矢印の起点。\"self\" / \"boss\" / actor 名等");

                    var durSa = (float)(action.Duration ?? 5.0);
                    ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputFloat("duration (s)##action-dur-sa", ref durSa)) { action.Duration = durSa <= 0 ? null : durSa; _dirty = true; }
                    var color = action.Color ?? "#00FF00";
                    ImGui.SetNextItemWidth(140f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputText("color (#RRGGBB)##action-color-sa", ref color, 16)) { action.Color = color; _dirty = true; }
                    DrawSafeZoneSubEditor(action);
                    break;
                }
                case "field_marker":
                {
                    var shape = action.Shape ?? "circle";
                    var sIdx = Array.IndexOf(FieldShapes, shape);
                    if (sIdx < 0) sIdx = 0;
                    var shapeLabels = Localization.LocalizeAll(FieldShapes, Localization.FieldShape);
                    ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
                    if (ImGui.Combo("形状##action-shape", ref sIdx, shapeLabels, shapeLabels.Length))
                    {
                        action.Shape = FieldShapes[sIdx];
                        _dirty = true;
                    }
                    var rad = (float)(action.Radius ?? 3.0);
                    ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputFloat("半径 (m)##action-fm-rad", ref rad)) { action.Radius = rad <= 0 ? null : rad; _dirty = true; }
                    var durFm = (float)(action.Duration ?? 5.0);
                    ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputFloat("表示秒数 (s)##action-dur-fm", ref durFm)) { action.Duration = durFm <= 0 ? null : durFm; _dirty = true; }
                    var colorFm = action.Color ?? "#00FF00";
                    ImGui.SetNextItemWidth(140f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputText("色 (#RRGGBB)##action-color-fm", ref colorFm, 16)) { action.Color = colorFm; _dirty = true; }
                    DrawSafeZoneSubEditor(action);
                    break;
                }
                case "proximity_feedback":
                {
                    var tol = (float)(action.Tolerance ?? 3.0);
                    ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputFloat("tolerance (m)##action-pf-tol", ref tol)) { action.Tolerance = tol <= 0 ? null : tol; _dirty = true; }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("安置に入った/出た判定の半径");

                    var inS = action.InSound ?? string.Empty;
                    ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputText("in_sound##action-pf-in", ref inS, 256)) { action.InSound = inS; _dirty = true; }
                    var outS = action.OutSound ?? string.Empty;
                    ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputText("out_sound##action-pf-out", ref outS, 256)) { action.OutSound = outS; _dirty = true; }
                    var showD = action.ShowDistance ?? false;
                    if (ImGui.Checkbox("距離をオーバーレイ表示##action-pf-show", ref showD)) { action.ShowDistance = showD; _dirty = true; }
                    DrawSafeZoneSubEditor(action);
                    break;
                }
                case "chain_trigger":
                {
                    var tid = action.TriggerId ?? string.Empty;
                    ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputText("trigger_id##action-ct-id", ref tid, 128)) { action.TriggerId = tid; _dirty = true; }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("連鎖発動するトリガーの id（同 zone 内のもの）");
                    // 同ファイル内のトリガー候補
                    if (_workingCopy is not null)
                    {
                        ImGui.SameLine();
                        if (ImGui.SmallButton("選択##action-ct-pick"))
                        {
                            ImGui.OpenPopup("chain-trigger-pick");
                        }
                        if (ImGui.BeginPopup("chain-trigger-pick"))
                        {
                            foreach (var t in _workingCopy.Triggers)
                            {
                                if (string.IsNullOrEmpty(t.Id)) continue;
                                if (ImGui.Selectable($"{t.Id}{(string.IsNullOrEmpty(t.Name) ? "" : $"  ({t.Name})")}"))
                                {
                                    action.TriggerId = t.Id;
                                    _dirty = true;
                                }
                            }
                            ImGui.EndPopup();
                        }
                    }
                    break;
                }
                case "set_variable":
                {
                    // set_variable はトリガー定義側の trigger.set_variable で扱う設計のため、
                    // アクションとして使うパスは JSON 直編集を推奨する旨を示す
                    ImGui.TextWrapped("set_variable はトリガー定義側の trigger.set_variable で設定するのが標準です。" +
                                      "アクションとして使う場合は JSON を直接編集してください。");
                    break;
                }
                default:
                    ImGui.TextDisabled($"({action.Type} は専用 UI 未実装。JSON 直接編集を推奨)");
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
        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();
        ImGui.TextDisabled("クイック追加：");
        ImGui.SameLine();
        DrawQuickAddButtons(trigger);
    }

    private void BeginAutoGeneration()
    {
        if (_workingCopy is null || _autoGenerator is null)
        {
            return;
        }
        var agg = _recordingScanner.Aggregate(_workingZone);
        var party = _recordingScanner.ListPartyMembers(_workingZone);
        _pendingAutoGen = _autoGenerator.Generate(agg, _workingCopy.Triggers, party);
        ImGui.OpenPopup("auto-gen-preview");
    }

    private void DrawAutoGenPreviewPopup()
    {
        ImGui.SetNextWindowSize(new Vector2(640f * ImGuiHelpers.GlobalScale, 540f * ImGuiHelpers.GlobalScale));
        if (!ImGui.BeginPopupModal("auto-gen-preview", ImGuiWindowFlags.NoCollapse))
        {
            return;
        }
        if (_workingCopy is null || _pendingAutoGen is null)
        {
            ImGui.EndPopup();
            return;
        }

        var result = _pendingAutoGen;
        ImGui.TextWrapped(
            "録画にあったボスのキャストごとに、Lumina の AoE データを参照して" +
            "「TTS + 視覚通知（円形 AoE / 外周回避 / コーン等）」を含むトリガーを生成します。" +
            "「適用」を押すと作業コピーに追加されます（保存はあなたが「保存」ボタンを押すまで反映されません）。");
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.5f, 0.95f, 0.55f, 1f),
            $"生成予定: {result.Generated.Count} 件 / スキップ: {result.Skipped.Count} 件");
        ImGui.Spacing();

        if (ImGui.BeginChild("##auto-gen-list", new Vector2(-1, 380f * ImGuiHelpers.GlobalScale), true))
        {
            if (result.Generated.Count > 0)
            {
                ImGui.TextColored(new Vector4(0.6f, 0.85f, 1f, 1f), "新規トリガー（生成予定）：");
                foreach (var t in result.Generated)
                {
                    var actionTypes = string.Join(" + ", t.Actions.ConvertAll(a => a.Type));
                    ImGui.Bullet();
                    ImGui.SameLine(0, 0);
                    ImGui.TextWrapped($"{t.Name}  ({t.Match?.CastId})  → [{actionTypes}]");
                }
                ImGui.Spacing();
            }
            if (result.Skipped.Count > 0)
            {
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "スキップ（既存と重複など）：");
                foreach (var s in result.Skipped)
                {
                    ImGui.Bullet();
                    ImGui.SameLine(0, 0);
                    ImGui.TextWrapped(s);
                }
            }
        }
        ImGui.EndChild();

        ImGui.Spacing();
        if (ImGui.Button($"{result.Generated.Count} 件を作業コピーに追加",
            new Vector2(280f * ImGuiHelpers.GlobalScale, 0)))
        {
            foreach (var t in result.Generated)
            {
                _workingCopy.Triggers.Add(t);
            }
            _dirty = true;
            _pendingAutoGen = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("キャンセル##auto-gen-cancel"))
        {
            _pendingAutoGen = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    /// <summary>
    /// よく使うアクション組合せを 1 クリックで追加するクイック追加ボタン群。
    /// 「カータライズの範囲を出したい」のような典型ケースを最短で組める。
    /// </summary>
    private void DrawQuickAddButtons(TriggerDefinition trigger)
    {
        var castName = trigger.Match?.CastName ?? trigger.Name ?? "AoE";

        if (ImGui.SmallButton("ボス AoE 円##qa-aoe"))
        {
            // 「ボス位置を中心とする円形 AoE」を field_marker として追加
            trigger.Actions.Add(new ActionDefinition
            {
                Type = "field_marker",
                Shape = "circle",
                Radius = 8.0,
                Duration = 5.0,
                Color = "#FF6464",
                SafeZone = new SafeZoneCalculation { Method = "boss_relative" },
            });
            _dirty = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(
            "ボス中心の赤い円をフィールドに描画。\n" +
            "半径 8m / 持続 5 秒 / safe_zone=boss_relative。\n" +
            "保存後は半径や色を編集可能。");

        ImGui.SameLine();
        if (ImGui.SmallButton("ボス前方コーン##qa-cone"))
        {
            trigger.Actions.Add(new ActionDefinition
            {
                Type = "arena_view",
                Gimmick = "cone",
                Direction = "N",
                FanDeg = 90,
                Callout = $"前方回避：{castName}",
                Duration = 5.0,
                ArenaRadius = 20.0,
            });
            _dirty = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(
            "俯瞰アリーナ図に「北向きの 90 度扇形」を表示。\n" +
            "実際のボスの向きと合わせるには direction を編集。");

        ImGui.SameLine();
        if (ImGui.SmallButton("外周回避##qa-outer"))
        {
            trigger.Actions.Add(new ActionDefinition
            {
                Type = "arena_view",
                Gimmick = "outer_ring",
                Callout = $"中央安置：{castName}",
                Duration = 5.0,
                ArenaRadius = 20.0,
            });
            _dirty = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(
            "俯瞰アリーナ図に「外周赤・中央緑」の安置パターンを表示。\n" +
            "全体 AoE で中央に集まるタイプ向け。");

        ImGui.SameLine();
        if (ImGui.SmallButton("散開##qa-scatter"))
        {
            trigger.Actions.Add(new ActionDefinition
            {
                Type = "arena_view",
                Gimmick = "scatter",
                Callout = $"散開：{castName}",
                Duration = 5.0,
                ArenaRadius = 20.0,
            });
            _dirty = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(
            "俯瞰アリーナ図に「4 方向散開」を表示。");

        ImGui.SameLine();
        if (ImGui.SmallButton("タイマーバー##qa-timer"))
        {
            trigger.Actions.Add(new ActionDefinition
            {
                Type = "timer_bar",
                Label = castName,
                Duration = 5.0,
                Color = "#FBBF24",
            });
            _dirty = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(
            "画面右下にカウントダウンバー（持続 5 秒）。\n" +
            "デバフや次イベントまでの時間表示に。");
    }

    private void DrawAggregatePanel(string zone)
    {
        var agg = _recordingScanner.Aggregate(zone);
        ImGui.TextDisabled($"録画ファイル {agg.RecordingFileCount} / 戦闘 {agg.BattleCount} / 総イベント {agg.TotalEventCount}");
        ImGui.SameLine(0, 24f);
        ImGui.Checkbox("タイムライン表示", ref _aggregateAsTimeline);
        ImGui.SameLine(0, 24f);
        ImGui.Checkbox("自分・PT のイベントを隠す", ref _hideSelfEvents);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("status_gain / status_update / hp_change のうち、target / source が PT メンバー名のものを除外。\n" +
                             "ボスのキャストやステータスだけに絞れる。");
        }
        ImGui.SameLine(0, 24f);
        ImGui.Checkbox("status を隠す", ref _hideStatusEvents);
        ImGui.Spacing();

        // フィルタ後のイベントを生成
        var filteredEvents = ApplyEventFilters(agg);

        if (filteredEvents.Count == 0)
        {
            ImGui.TextWrapped("表示できるイベントがありません。フィルタを緩めるか、" +
                "/echoes record on で録画してください。");
            return;
        }

        if (_aggregateAsTimeline)
        {
            var filteredAgg = new Recording.AggregatedEvents(
                filteredEvents, agg.BattleCount, agg.TotalEventCount, agg.RecordingFileCount);
            _timelineRenderer.Draw(filteredAgg);
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
                ImGui.SameLine();
                if (ImGui.Button("攻略ギミックに追加"))
                {
                    CreateStrategyMechanicFromKey(selected);
                }
            }
            return;
        }

        if (ImGui.BeginTable("##aggregate-table", 7,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp |
            ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY))
        {
            ImGui.TableSetupColumn("種別", ImGuiTableColumnFlags.WidthFixed, 100f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthFixed, 100f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("名前", ImGuiTableColumnFlags.WidthStretch, 2.0f);
            ImGui.TableSetupColumn("発動者/対象", ImGuiTableColumnFlags.WidthStretch, 1.5f);
            ImGui.TableSetupColumn("観測回数", ImGuiTableColumnFlags.WidthFixed, 80f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("初回時刻", ImGuiTableColumnFlags.WidthFixed, 90f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("攻略", ImGuiTableColumnFlags.WidthFixed, 86f * ImGuiHelpers.GlobalScale);
            ImGui.TableHeadersRow();

            foreach (var ev in filteredEvents)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(ev.Key.Type);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(ev.Key.Id ?? "—");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(ev.Key.Name ?? "—");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(ev.Key.Source ?? ev.Key.Target ?? "—");
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{ev.Count}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{ev.FirstSeenSeconds:0.0}s");
                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"追加##strategy-{ev.Key.Type}-{ev.Key.Id}-{ev.FirstSeenSeconds:0.0}"))
                {
                    CreateStrategyMechanicFromEvent(ev);
                }
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

    private void CreateStrategyMechanicFromKey(EventKey key)
    {
        if (_workingCopy is null)
        {
            return;
        }

        var ev = _recordingScanner.Aggregate(_workingZone).Events.FirstOrDefault(e => e.Key.Equals(key));
        if (ev is not null)
        {
            CreateStrategyMechanicFromEvent(ev);
            return;
        }

        var profile = EnsureActiveStrategyProfile();
        var id = UniqueMechanicId(profile, SuggestMechanicPrefix(key));
        var prediction = new RecordingTimelinePrediction(
            EventType: key.Type,
            RelativeSeconds: 0,
            Label: key.Name ?? key.Id ?? key.Type,
            Id: key.Id ?? string.Empty,
            Source: key.Source,
            Target: key.Target,
            ObservedCount: 1,
            OccurrenceIndex: 0,
            OccurrenceSeenCount: 1,
            Confidence: 1.0,
            TimeJitterSeconds: 0);
        profile.Mechanics.Add(StrategyPlanResolver.CreateMechanicDraft(prediction, id));
        _dirty = true;
    }

    private void CreateStrategyMechanicFromEvent(AggregatedEvent ev)
    {
        var profile = EnsureActiveStrategyProfile();
        var agg = _recordingScanner.Aggregate(_workingZone);
        var battleCount = Math.Max(1, agg.BattleCount);
        var occurrences = RecordingPredictionPlanner.GetOccurrences(ev);
        var prefix = SuggestMechanicPrefix(ev.Key);
        foreach (var occurrence in occurrences)
        {
            var id = UniqueMechanicId(profile, prefix);
            var baseLabel = ev.Key.Name ?? ev.Key.Id ?? ev.Key.Type;
            var prediction = new RecordingTimelinePrediction(
                EventType: ev.Key.Type,
                RelativeSeconds: occurrence.RepresentativeTimeSeconds,
                Label: occurrences.Count > 1 ? $"{baseLabel} #{occurrence.Index + 1}" : baseLabel,
                Id: ev.Key.Id ?? string.Empty,
                Source: ev.Key.Source,
                Target: ev.Key.Target,
                ObservedCount: ev.Count,
                OccurrenceIndex: occurrence.Index,
                OccurrenceSeenCount: occurrence.SeenCount,
                Confidence: Math.Clamp((double)occurrence.SeenCount / battleCount, 0.0, 1.0),
                TimeJitterSeconds: CalculateEventJitter(occurrence.ObservedTimesSeconds));
            profile.Mechanics.Add(StrategyPlanResolver.CreateMechanicDraft(prediction, id));
        }
        _dirty = true;
    }

    private StrategyProfile EnsureActiveStrategyProfile()
    {
        if (_workingCopy is null)
        {
            throw new InvalidOperationException("No working trigger file.");
        }

        var profile = StrategyPlanResolver.SelectActiveProfile(_workingCopy);
        if (profile is not null)
        {
            return profile;
        }

        profile = CreateDefaultStrategyProfile("default", "Default party strategy");
        _workingCopy.StrategyProfiles.Add(profile);
        _workingCopy.ActiveStrategyProfileId = profile.Id;
        return profile;
    }

    private static string SuggestMechanicPrefix(EventKey key)
    {
        var raw = key.Name ?? key.Id ?? key.Type;
        var normalized = new string(raw
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray()).Trim('_');
        return string.IsNullOrWhiteSpace(normalized) ? "mechanic" : normalized;
    }

    private static double CalculateEventJitter(IReadOnlyList<double> times)
    {
        if (times.Count <= 1)
        {
            return 0;
        }

        return times.Max() - times.Min();
    }

    private static string SuggestId(EventKey key) => key.Type switch
    {
        "cast_start" or "cast_complete" or "cast_cancel" => $"cast_{key.Id ?? key.Name ?? "x"}".ToLowerInvariant().Replace(" ", "_"),
        "action_used" => $"action_{key.Id ?? key.Name ?? "x"}".ToLowerInvariant().Replace(" ", "_"),
        "status_gain" or "status_lose" or "status_update" => $"status_{key.Id ?? key.Name ?? "x"}".ToLowerInvariant().Replace(" ", "_"),
        "hp_change" => $"hp_{key.Source ?? "actor"}",
        "object_appear" or "object_disappear" => $"object_{key.Id ?? key.Name ?? "x"}".ToLowerInvariant().Replace(" ", "_"),
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
            case "action_used":
                m.ActionId = key.Id;
                m.ActionName = key.Name;
                m.Source = key.Source;
                break;
            case "hp_change":
                m.Actor = key.Source;
                break;
            case "object_appear":
            case "object_disappear":
                m.Actor = key.Name ?? key.Source;
                break;
        }
        return m;
    }

    private void DrawStrategyPanel()
    {
        if (_workingCopy is null)
        {
            return;
        }

        ImGui.TextWrapped("Party-specific strategy profiles are saved in this trigger file. Use them for group-specific spreads, callouts, safe zones, and minimap markers.");
        ImGui.Spacing();

        if (_workingCopy.StrategyProfiles.Count == 0)
        {
            if (ImGui.Button("Add default profile"))
            {
                _workingCopy.StrategyProfiles.Add(CreateDefaultStrategyProfile("default", "Default party strategy"));
                _workingCopy.ActiveStrategyProfileId = "default";
                _dirty = true;
            }
            return;
        }

        DrawStrategyProfileSelector();
        var profile = StrategyPlanResolver.SelectActiveProfile(_workingCopy) ?? _workingCopy.StrategyProfiles[0];

        ImGui.Spacing();
        ImGui.Separator();
        DrawStrategyProfileEditor(profile);

        ImGui.Spacing();
        ImGui.Separator();
        DrawStrategyPositionsEditor(profile);

        ImGui.Spacing();
        ImGui.Separator();
        DrawMechanicStrategiesEditor(profile);
        DrawStrategyAttachPopup();
    }

    private void DrawStrategyProfileSelector()
    {
        if (_workingCopy is null)
        {
            return;
        }

        var profiles = _workingCopy.StrategyProfiles;
        var labels = profiles
            .Select(p => string.IsNullOrWhiteSpace(p.Name) ? p.Id : $"{p.Name} ({p.Id})")
            .ToArray();
        var selectedIndex = Math.Max(0, profiles.FindIndex(p =>
            string.Equals(p.Id, _workingCopy.ActiveStrategyProfileId, StringComparison.OrdinalIgnoreCase)));

        ImGui.SetNextItemWidth(360f * ImGuiHelpers.GlobalScale);
        if (ImGui.Combo("Active strategy profile", ref selectedIndex, labels, labels.Length))
        {
            _workingCopy.ActiveStrategyProfileId = profiles[selectedIndex].Id;
            _dirty = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("Add profile"))
        {
            var id = UniqueStrategyId("profile");
            var profile = CreateDefaultStrategyProfile(id, $"Strategy {profiles.Count + 1}");
            profiles.Add(profile);
            _workingCopy.ActiveStrategyProfileId = profile.Id;
            _dirty = true;
        }
    }

    private void DrawStrategyProfileEditor(StrategyProfile profile)
    {
        ImGui.TextUnformatted("Profile");
        var enabled = profile.Enabled;
        if (ImGui.Checkbox("Enabled##strategy-profile-enabled", ref enabled))
        {
            profile.Enabled = enabled;
            _dirty = true;
        }

        var id = profile.Id;
        ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("Id##strategy-profile-id", ref id, 64))
        {
            var oldId = profile.Id;
            profile.Id = string.IsNullOrWhiteSpace(id) ? oldId : id.Trim();
            if (_workingCopy?.ActiveStrategyProfileId == oldId)
            {
                _workingCopy.ActiveStrategyProfileId = profile.Id;
            }
            _dirty = true;
        }

        var name = profile.Name;
        ImGui.SetNextItemWidth(320f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("Name##strategy-profile-name", ref name, 128))
        {
            profile.Name = name;
            _dirty = true;
        }

        var radius = (float)(profile.ArenaRadius ?? 20.0);
        ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputFloat("Arena radius##strategy-arena-radius", ref radius, 0.5f, 1.0f, "%.1f"))
        {
            profile.ArenaRadius = radius <= 0 ? null : radius;
            _dirty = true;
        }

        var description = profile.Description ?? string.Empty;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextMultiline("Description##strategy-description", ref description, 1024,
                new Vector2(-1, 60f * ImGuiHelpers.GlobalScale)))
        {
            profile.Description = string.IsNullOrWhiteSpace(description) ? null : description;
            _dirty = true;
        }
    }

    private void DrawStrategyPositionsEditor(StrategyProfile profile)
    {
        ImGui.TextUnformatted("Spread positions");
        ImGui.SameLine();
        if (ImGui.SmallButton("Add position"))
        {
            profile.SpreadPositions.Add(new StrategyPosition
            {
                Slot = $"P{profile.SpreadPositions.Count + 1}",
                Label = $"P{profile.SpreadPositions.Count + 1}",
                Color = "#F472B6",
            });
            _dirty = true;
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Fill 8-way missing"))
        {
            foreach (var pos in CreateEightWaySpreadPositions())
            {
                if (profile.SpreadPositions.Any(p => string.Equals(p.Slot, pos.Slot, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                profile.SpreadPositions.Add(pos);
            }
            _dirty = true;
        }

        if (!ImGui.BeginTable("##strategy-positions-table", 9,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable))
        {
            return;
        }

        ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed, 56f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthFixed, 70f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Role", ImGuiTableColumnFlags.WidthFixed, 82f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Job", ImGuiTableColumnFlags.WidthFixed, 56f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("X", ImGuiTableColumnFlags.WidthFixed, 70f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Z", ImGuiTableColumnFlags.WidthFixed, 70f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Color", ImGuiTableColumnFlags.WidthFixed, 88f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Note", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 68f * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        for (var i = 0; i < profile.SpreadPositions.Count; i++)
        {
            var pos = profile.SpreadPositions[i];
            ImGui.PushID($"strategy-pos-{i}");
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var slot = pos.Slot;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##slot", ref slot, 32))
            {
                pos.Slot = slot;
                _dirty = true;
            }

            ImGui.TableNextColumn();
            var label = pos.Label ?? string.Empty;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##label", ref label, 32))
            {
                pos.Label = string.IsNullOrWhiteSpace(label) ? null : label;
                _dirty = true;
            }

            ImGui.TableNextColumn();
            var role = pos.Role ?? string.Empty;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##role", ref role, 32))
            {
                pos.Role = string.IsNullOrWhiteSpace(role) ? null : role;
                _dirty = true;
            }

            ImGui.TableNextColumn();
            var job = pos.Job ?? string.Empty;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##job", ref job, 16))
            {
                pos.Job = string.IsNullOrWhiteSpace(job) ? null : job;
                _dirty = true;
            }

            ImGui.TableNextColumn();
            var x = (float)pos.X;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputFloat("##x", ref x, 0.5f, 1f, "%.1f"))
            {
                pos.X = x;
                _dirty = true;
            }

            ImGui.TableNextColumn();
            var z = (float)pos.Z;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputFloat("##z", ref z, 0.5f, 1f, "%.1f"))
            {
                pos.Z = z;
                _dirty = true;
            }

            ImGui.TableNextColumn();
            var color = pos.Color ?? string.Empty;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##color", ref color, 16))
            {
                pos.Color = string.IsNullOrWhiteSpace(color) ? null : color;
                _dirty = true;
            }

            ImGui.TableNextColumn();
            var note = pos.Note ?? string.Empty;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##note", ref note, 128))
            {
                pos.Note = string.IsNullOrWhiteSpace(note) ? null : note;
                _dirty = true;
            }

            ImGui.TableNextColumn();
            if (ImGui.SmallButton("Delete"))
            {
                profile.SpreadPositions.RemoveAt(i);
                _dirty = true;
                ImGui.PopID();
                break;
            }

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void DrawMechanicStrategiesEditor(StrategyProfile profile)
    {
        ImGui.TextUnformatted("Mechanics");
        ImGui.SameLine();
        if (ImGui.SmallButton("Add mechanic"))
        {
            var id = UniqueMechanicId(profile, "mechanic");
            profile.Mechanics.Add(new MechanicStrategy
            {
                Id = id,
                Label = $"Mechanic {profile.Mechanics.Count + 1}",
                Time = 0,
                AdvanceWarningSec = 5,
                Gimmick = "scatter",
                Color = "#F472B6",
            });
            _dirty = true;
        }

        if (profile.Mechanics.Count == 0)
        {
            ImGui.TextDisabled("Add mechanics here when your party uses custom spreads, stacks, bait order, or safe calls.");
            return;
        }

        for (var i = 0; i < profile.Mechanics.Count; i++)
        {
            var mechanic = profile.Mechanics[i];
            ImGui.PushID($"strategy-mechanic-{i}");
            var title = string.IsNullOrWhiteSpace(mechanic.Label) ? mechanic.Id : mechanic.Label;
            if (ImGui.CollapsingHeader($"{title}##strategy-mechanic-header", ImGuiTreeNodeFlags.DefaultOpen))
            {
                DrawMechanicStrategyEditor(profile, mechanic, i);
            }
            ImGui.PopID();
        }
    }

    private void DrawMechanicStrategyEditor(StrategyProfile profile, MechanicStrategy mechanic, int index)
    {
        var enabled = mechanic.Enabled;
        if (ImGui.Checkbox("Enabled##mechanic-enabled", ref enabled))
        {
            mechanic.Enabled = enabled;
            _dirty = true;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Delete mechanic"))
        {
            profile.Mechanics.RemoveAt(index);
            _dirty = true;
            return;
        }

        var id = mechanic.Id;
        ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("Id##mechanic-id", ref id, 64))
        {
            mechanic.Id = string.IsNullOrWhiteSpace(id) ? mechanic.Id : id.Trim();
            _dirty = true;
        }

        var label = mechanic.Label;
        ImGui.SetNextItemWidth(320f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("Label##mechanic-label", ref label, 128))
        {
            mechanic.Label = label;
            _dirty = true;
        }

        var time = (float)(mechanic.Time ?? 0);
        ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputFloat("Time (s)##mechanic-time", ref time, 0.5f, 1f, "%.1f"))
        {
            mechanic.Time = time <= 0 ? null : time;
            _dirty = true;
        }

        ImGui.SameLine();
        var duration = (float)(mechanic.Duration ?? 0);
        ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputFloat("Duration##mechanic-duration", ref duration, 0.5f, 1f, "%.1f"))
        {
            mechanic.Duration = duration <= 0 ? null : duration;
            _dirty = true;
        }

        ImGui.SameLine();
        var warn = (float)(mechanic.AdvanceWarningSec ?? 0);
        ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputFloat("Warn##mechanic-warn", ref warn, 0.5f, 1f, "%.1f"))
        {
            mechanic.AdvanceWarningSec = warn <= 0 ? null : warn;
            _dirty = true;
        }

        DrawMechanicAttachEditor(profile, mechanic);
        DrawMechanicEvidence(mechanic);

        var role = mechanic.Role ?? string.Empty;
        ImGui.SetNextItemWidth(160f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("Role filter##mechanic-role", ref role, 32))
        {
            mechanic.Role = string.IsNullOrWhiteSpace(role) ? null : role;
            _dirty = true;
        }

        ImGui.SameLine();
        var job = mechanic.Job ?? string.Empty;
        ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("Job filter##mechanic-job", ref job, 16))
        {
            mechanic.Job = string.IsNullOrWhiteSpace(job) ? null : job;
            _dirty = true;
        }

        var callout = mechanic.Callout ?? string.Empty;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("Callout##mechanic-callout", ref callout, 256))
        {
            mechanic.Callout = string.IsNullOrWhiteSpace(callout) ? null : callout;
            _dirty = true;
        }

        var warning = mechanic.WarningText ?? string.Empty;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("Warning text##mechanic-warning", ref warning, 256))
        {
            mechanic.WarningText = string.IsNullOrWhiteSpace(warning) ? null : warning;
            _dirty = true;
        }

        var gimmick = mechanic.Gimmick ?? "scatter";
        var gimmickIndex = Array.IndexOf(ArenaViewGimmicks, gimmick);
        if (gimmickIndex < 0)
        {
            gimmickIndex = 0;
        }
        ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
        if (ImGui.Combo("Minimap gimmick##mechanic-gimmick", ref gimmickIndex, ArenaViewGimmicks, ArenaViewGimmicks.Length))
        {
            mechanic.Gimmick = ArenaViewGimmicks[gimmickIndex];
            _dirty = true;
        }

        ImGui.SameLine();
        var color = mechanic.Color ?? string.Empty;
        ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("Color##mechanic-color", ref color, 16))
        {
            mechanic.Color = string.IsNullOrWhiteSpace(color) ? null : color;
            _dirty = true;
        }

        var positions = string.Join(", ", mechanic.Positions);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("Positions CSV##mechanic-positions", ref positions, 256))
        {
            mechanic.Positions = positions
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            _dirty = true;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Leave empty to use all spread positions, or enter slots like MT,H1,D1.");
        }

        var safeZoneAction = new ActionDefinition { SafeZone = mechanic.SafeZone };
        DrawSafeZoneSubEditor(safeZoneAction);
        mechanic.SafeZone = safeZoneAction.SafeZone;
    }

    private void DrawMechanicAttachEditor(StrategyProfile profile, MechanicStrategy mechanic)
    {
        var attached = mechanic.AttachedTo?.CastName ?? mechanic.AttachedTo?.CastId ?? "(timeline time)";
        ImGui.TextDisabled($"Attached: {attached}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Attach observed cast"))
        {
            _attachStrategyProfileId = profile.Id;
            _attachStrategyMechanicId = mechanic.Id;
            ImGui.OpenPopup("strategy-attach-popup");
        }
        ImGui.SameLine();
        if (mechanic.AttachedTo is not null && ImGui.SmallButton("Clear attach"))
        {
            mechanic.AttachedTo = null;
            _dirty = true;
        }
    }

    private static void DrawMechanicEvidence(MechanicStrategy mechanic)
    {
        if (mechanic.ObservedCount is null &&
            mechanic.Confidence is null &&
            mechanic.TimeJitterSeconds is null)
        {
            return;
        }

        var confidence = mechanic.Confidence is { } c ? $"{c:P0}" : "-";
        var seen = mechanic.OccurrenceSeenCount is { } occurrenceSeen
            ? $"{occurrenceSeen}/{mechanic.ObservedCount ?? occurrenceSeen}"
            : $"{mechanic.ObservedCount ?? 0}";
        var jitter = mechanic.TimeJitterSeconds is { } j ? $"{j:0.0}s" : "-";
        ImGui.TextDisabled($"Learned: {mechanic.SourceEventType ?? "event"} / seen {seen} / confidence {confidence} / jitter {jitter}");
    }

    private void DrawStrategyAttachPopup()
    {
        ImGui.SetNextWindowSize(new Vector2(560f * ImGuiHelpers.GlobalScale, 420f * ImGuiHelpers.GlobalScale));
        if (!ImGui.BeginPopup("strategy-attach-popup"))
        {
            return;
        }

        if (_workingCopy is null ||
            string.IsNullOrEmpty(_attachStrategyProfileId) ||
            string.IsNullOrEmpty(_attachStrategyMechanicId))
        {
            ImGui.EndPopup();
            return;
        }

        var profile = _workingCopy.StrategyProfiles.FirstOrDefault(p =>
            string.Equals(p.Id, _attachStrategyProfileId, StringComparison.OrdinalIgnoreCase));
        var mechanic = profile?.Mechanics.FirstOrDefault(m =>
            string.Equals(m.Id, _attachStrategyMechanicId, StringComparison.OrdinalIgnoreCase));
        if (profile is null || mechanic is null)
        {
            ImGui.EndPopup();
            return;
        }

        ImGui.TextWrapped("Choose an observed cast from recordings. The mechanic will follow that cast timing on future pulls.");
        ImGui.Spacing();

        var agg = _recordingScanner.Aggregate(_workingZone);
        var filtered = ApplyEventFilters(agg);
        if (ImGui.BeginChild("##strategy-attach-list", new Vector2(-1, 320f * ImGuiHelpers.GlobalScale), true))
        {
            foreach (var ev in filtered)
            {
                if (ev.Key.Type != "cast_start")
                {
                    continue;
                }

                var label = $"[{ev.FirstSeenSeconds:0.0}s]  {ev.Key.Name ?? "?"}  ({ev.Key.Id ?? "-"})  x{ev.Count}";
                if (ImGui.Selectable(label))
                {
                    mechanic.AttachedTo = new MatchCondition
                    {
                        CastId = ev.Key.Id,
                        CastName = ev.Key.Name,
                    };
                    mechanic.Time = ev.FirstSeenSeconds;
                    _dirty = true;
                    ImGui.CloseCurrentPopup();
                }
            }
        }
        ImGui.EndChild();

        if (ImGui.Button("Cancel##strategy-attach-cancel"))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private string UniqueStrategyId(string prefix)
    {
        if (_workingCopy is null)
        {
            return prefix;
        }

        var index = _workingCopy.StrategyProfiles.Count + 1;
        string id;
        do
        {
            id = $"{prefix}_{index++}";
        }
        while (_workingCopy.StrategyProfiles.Any(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)));
        return id;
    }

    private static string UniqueMechanicId(StrategyProfile profile, string prefix)
    {
        var index = profile.Mechanics.Count + 1;
        string id;
        do
        {
            id = $"{prefix}_{index++}";
        }
        while (profile.Mechanics.Any(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)));
        return id;
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
        var showPredicted = _workingCopy.AutoSettings.ShowPredictedCasts;
        if (ImGui.Checkbox("show_predicted_casts（録画ベースの予測キャストをタイムラインに表示）", ref showPredicted))
        {
            _workingCopy.AutoSettings.ShowPredictedCasts = showPredicted;
            _dirty = true;
        }
        var autoTel = _workingCopy.AutoSettings.ShowAutoTelegraphs;
        if (ImGui.Checkbox("show_auto_telegraphs（敵キャストの AoE 範囲を自動でフィールドに描画）", ref autoTel))
        {
            _workingCopy.AutoSettings.ShowAutoTelegraphs = autoTel;
            _dirty = true;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Lumina Action.EffectRange / CastType を読んで円形 AoE を自動表示。\n" +
                             "Cone / Line は方向計算が未対応のため、半径だけ目安として円で出る。\n" +
                             "PT 内のプレイヤーキャストは無視。");
        }

        var showAllEnemyCasts = _workingCopy.AutoSettings.ShowAllEnemyCasts;
        if (ImGui.Checkbox("show_all_enemy_casts（AoE不明の敵キャストもミニマップに表示）", ref showAllEnemyCasts))
        {
            _workingCopy.AutoSettings.ShowAllEnemyCasts = showAllEnemyCasts;
            _dirty = true;
        }

        var showAutoAttacks = _workingCopy.AutoSettings.ShowAutoAttacks;
        if (ImGui.Checkbox("show_auto_attacks（AA/即時アクションを表示）", ref showAutoAttacks))
        {
            _workingCopy.AutoSettings.ShowAutoAttacks = showAutoAttacks;
            _dirty = true;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("AA は Dalamud 側で action id が観測できた場合だけ表示します。うるさい場合は OFF 推奨です。");
        }

        ImGui.Spacing();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("予測アドバンス警告:");
        ImGui.SameLine();
        var warnEnabled = _workingCopy.AutoSettings.PredictAdvanceWarningSec is > 0;
        if (ImGui.Checkbox("##predict-warn-enable", ref warnEnabled))
        {
            _workingCopy.AutoSettings.PredictAdvanceWarningSec = warnEnabled ? AutoSettings.DefaultPredictAdvanceWarningSec : null;
            _dirty = true;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("録画から拾った各キャストについて、開始の何秒前に「次：〇〇」と TTS で先行通知するか。\n" +
                             "0 / OFF で無効。トリガー定義に同じ cast_id がある場合は重複通知しない。");
        }
        if (warnEnabled)
        {
            ImGui.SameLine();
            var warnSec = (float)(_workingCopy.AutoSettings.PredictAdvanceWarningSec ?? AutoSettings.DefaultPredictAdvanceWarningSec);
            ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
            if (ImGui.InputFloat("秒前##predict-warn-sec", ref warnSec))
            {
                _workingCopy.AutoSettings.PredictAdvanceWarningSec = warnSec <= 0 ? null : warnSec;
                _dirty = true;
            }
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

    private void DrawNotesPanel()
    {
        if (_workingCopy is null)
        {
            return;
        }
        ImGui.TextWrapped("「ここで軽減」「ここで LB」を書いておくと、ライブタイムラインに表示されます。" +
            "時刻は秒で直接指定するか、「特定キャストに紐付け」で録画から自動解決させられます。" +
            "advance_warning_sec を設定するとその秒数前に TTS / オーバーレイで先行通知。");
        ImGui.Spacing();

        if (ImGui.Button("新規ノート##new-note"))
        {
            _workingCopy.Notes.Add(new TimelineNote
            {
                Id = $"note_{_workingCopy.Notes.Count + 1}",
                Time = 0,
                Label = "",
            });
            _dirty = true;
        }
        ImGui.SameLine();
        if (ImGui.Button("録画キャストから一括追加"))
        {
            ImGui.OpenPopup("note-from-cast-popup");
        }
        DrawNoteFromCastPopup();
        ImGui.Spacing();

        if (_workingCopy.Notes.Count == 0)
        {
            ImGui.TextDisabled("ノートがありません。「新規ノート」or「録画キャストから一括追加」で追加してください。");
            return;
        }

        if (ImGui.BeginTable("##notes-table", 7,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthStretch, 1.0f);
            ImGui.TableSetupColumn("時刻(s)", ImGuiTableColumnFlags.WidthFixed, 80f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("ラベル", ImGuiTableColumnFlags.WidthStretch, 2.0f);
            ImGui.TableSetupColumn("先行通知(s)", ImGuiTableColumnFlags.WidthFixed, 90f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("ロール", ImGuiTableColumnFlags.WidthFixed, 80f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("色", ImGuiTableColumnFlags.WidthFixed, 90f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 70f * ImGuiHelpers.GlobalScale);
            ImGui.TableHeadersRow();

            for (int i = 0; i < _workingCopy.Notes.Count; i++)
            {
                var note = _workingCopy.Notes[i];
                ImGui.PushID($"note-{i}");
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                var id = note.Id;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("##id", ref id, 32)) { note.Id = id; _dirty = true; }

                ImGui.TableNextColumn();
                if (note.AttachedTo is not null)
                {
                    // 紐付け中：時刻入力は無効化、cast_id を表示し編集ボタンで切替
                    var attachLabel = note.AttachedTo.CastId ?? note.AttachedTo.CastName ?? "—";
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(new Vector4(0.4f, 0.85f, 1f, 1f), $"📌 {attachLabel}");
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("特定キャストに紐付け中。クリックすると解除");
                    if (ImGui.IsItemClicked())
                    {
                        note.AttachedTo = null;
                        _dirty = true;
                    }
                }
                else
                {
                    var time = (float)note.Time;
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.InputFloat("##time", ref time, 1.0f, 5.0f, "%.1f")) { note.Time = time; _dirty = true; }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("戦闘相対秒。録画キャストに紐付けたい時はラベル右横の 📎 ボタン");
                }

                ImGui.TableNextColumn();
                var label = note.Label;
                ImGui.SetNextItemWidth(-32f * ImGuiHelpers.GlobalScale);
                if (ImGui.InputText("##label", ref label, 128)) { note.Label = label; _dirty = true; }
                ImGui.SameLine();
                if (ImGui.SmallButton("📎"))
                {
                    _attachNoteIndex = i;
                    ImGui.OpenPopup("note-attach-popup");
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("録画されたキャストに紐付けて時刻を自動解決");

                ImGui.TableNextColumn();
                var warn = (float)(note.AdvanceWarningSec ?? 0);
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputFloat("##warn", ref warn, 0.5f, 1.0f, "%.1f"))
                {
                    note.AdvanceWarningSec = warn <= 0 ? null : warn;
                    _dirty = true;
                }

                ImGui.TableNextColumn();
                var role = note.Role ?? string.Empty;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("##role", ref role, 16))
                {
                    note.Role = string.IsNullOrEmpty(role) ? null : role;
                    _dirty = true;
                }

                ImGui.TableNextColumn();
                var color = note.Color ?? string.Empty;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("##color", ref color, 8))
                {
                    note.Color = string.IsNullOrEmpty(color) ? null : color;
                    _dirty = true;
                }

                ImGui.TableNextColumn();
                if (ImGui.SmallButton("削除"))
                {
                    _workingCopy.Notes.RemoveAt(i);
                    _dirty = true;
                    ImGui.PopID();
                    break;
                }

                ImGui.PopID();
            }
            ImGui.EndTable();
        }

        DrawNoteAttachPopup();
    }

    /// <summary>
    /// ノートを録画されたキャストに紐付けるためのピッカー popup。
    /// </summary>
    private void DrawNoteAttachPopup()
    {
        ImGui.SetNextWindowSize(new Vector2(500f * ImGuiHelpers.GlobalScale, 400f * ImGuiHelpers.GlobalScale));
        if (!ImGui.BeginPopup("note-attach-popup"))
        {
            return;
        }
        if (_attachNoteIndex < 0 || _workingCopy is null || _attachNoteIndex >= _workingCopy.Notes.Count)
        {
            ImGui.EndPopup();
            return;
        }
        var note = _workingCopy.Notes[_attachNoteIndex];
        ImGui.TextWrapped("録画から拾ったキャストにこのノートを紐付けます。" +
                          "選択するとノートの時刻が自動で「キャスト開始の相対秒」に追従します。");
        ImGui.Spacing();

        var agg = _recordingScanner.Aggregate(_workingZone);
        var filtered = ApplyEventFilters(agg);
        if (ImGui.BeginChild("##attach-list", new Vector2(-1, 320f * ImGuiHelpers.GlobalScale), true))
        {
            foreach (var ev in filtered)
            {
                if (ev.Key.Type != "cast_start") continue;
                var label = $"[{ev.FirstSeenSeconds:0.0}s]  {ev.Key.Name ?? "?"}  ({ev.Key.Id ?? "—"})  ×{ev.Count}";
                if (ImGui.Selectable(label))
                {
                    note.AttachedTo = new MatchCondition
                    {
                        CastId = ev.Key.Id,
                        CastName = ev.Key.Name,
                    };
                    _dirty = true;
                    ImGui.CloseCurrentPopup();
                }
            }
        }
        ImGui.EndChild();

        if (ImGui.Button("キャンセル##attach-cancel"))
        {
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    /// <summary>
    /// 録画キャストから一括ノート生成 popup。チェックを入れたものだけノートにする。
    /// </summary>
    private readonly HashSet<string> _bulkSelectedCastIds = new(StringComparer.OrdinalIgnoreCase);
    private float _bulkAdvanceWarn = 5f;

    private void DrawNoteFromCastPopup()
    {
        ImGui.SetNextWindowSize(new Vector2(560f * ImGuiHelpers.GlobalScale, 480f * ImGuiHelpers.GlobalScale));
        if (!ImGui.BeginPopupModal("note-from-cast-popup", ImGuiWindowFlags.NoCollapse))
        {
            return;
        }
        if (_workingCopy is null) { ImGui.EndPopup(); return; }

        ImGui.TextWrapped("録画されたキャストから一括でノートを生成します。チェックを入れたキャストごとに" +
                          "「📌 紐付けノート」が作成されます（時刻は自動解決）。");
        ImGui.Spacing();

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("先行通知秒数:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
        ImGui.InputFloat("##bulk-warn", ref _bulkAdvanceWarn, 0.5f, 1.0f, "%.1f");
        ImGui.Spacing();

        var agg = _recordingScanner.Aggregate(_workingZone);
        var filtered = ApplyEventFilters(agg);
        if (ImGui.BeginChild("##bulk-list", new Vector2(-1, 340f * ImGuiHelpers.GlobalScale), true))
        {
            foreach (var ev in filtered)
            {
                if (ev.Key.Type != "cast_start") continue;
                var key = ev.Key.Id ?? ev.Key.Name ?? "?";
                var checkedNow = _bulkSelectedCastIds.Contains(key);
                if (ImGui.Checkbox($"[{ev.FirstSeenSeconds:0.0}s]  {ev.Key.Name ?? "?"}  ({ev.Key.Id ?? "—"})  ×{ev.Count}##bulk-{key}",
                        ref checkedNow))
                {
                    if (checkedNow) _bulkSelectedCastIds.Add(key);
                    else _bulkSelectedCastIds.Remove(key);
                }
            }
        }
        ImGui.EndChild();

        if (ImGui.Button($"{_bulkSelectedCastIds.Count} 件のノートを生成", new Vector2(220f * ImGuiHelpers.GlobalScale, 0)))
        {
            foreach (var ev in filtered)
            {
                if (ev.Key.Type != "cast_start") continue;
                var key = ev.Key.Id ?? ev.Key.Name ?? "?";
                if (!_bulkSelectedCastIds.Contains(key)) continue;
                var label = ev.Key.Name ?? key;
                _workingCopy.Notes.Add(new TimelineNote
                {
                    Id = $"note_attached_{key.Replace("0x", "").Replace("#", "").ToLowerInvariant()}",
                    Label = label,
                    AttachedTo = new MatchCondition { CastId = ev.Key.Id, CastName = ev.Key.Name },
                    AdvanceWarningSec = _bulkAdvanceWarn > 0 ? _bulkAdvanceWarn : null,
                    Color = "#FCD34D",
                });
            }
            _dirty = true;
            _bulkSelectedCastIds.Clear();
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("キャンセル##bulk-cancel"))
        {
            _bulkSelectedCastIds.Clear();
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
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
        "store_position", "arena_view",
    };

    private static readonly string[] ArenaViewGimmicks =
    {
        "outer_ring", "inner_circle", "scatter", "stack", "cone",
    };

    private static readonly string[] ArenaViewDirections =
    {
        "N", "NE", "E", "SE", "S", "SW", "W", "NW",
    };

    private static readonly string[] DirectionFormats =
    {
        "cardinal", "cardinal_jp", "clock", "relative_jp", "degrees",
    };

    private static readonly string[] FieldShapes =
    {
        "circle", "square", "x_mark", "arrow",
    };

    private static readonly string[] SafeZoneMethods =
    {
        "fixed",
        "boss_relative",
        "marker_relative",
        "arena_center_relative",
        "inverse_of_telegraph",
        "party_member_relative",
        "find_actor_with_status",
        "find_actor_without_status",
        "find_actor_not_casting",
        "find_actor_by_distance",
        "midpoint",
        "between_actors",
        "line_perpendicular",
        "telegraph_gap",
        "intersection",
        "stored_position",
    };

    /// <summary>arena_view のギミック種類ごとの説明文（日本語）。</summary>
    private static string GimmickTooltip(string gimmick) => gimmick switch
    {
        "outer_ring" => "ボスから遠いほど危険、中央に安置の緑丸を描画。\n例：「無の肥大」「外周回避」のような全体 AoE",
        "inner_circle" => "ボス周囲が危険、外周が安置。\n例：「サークル AoE」「中央回避」のようなボス中心 AoE",
        "scatter" => "4 方向（北東南西）に散開ポジを描画。\n例：散開デバフ、ターゲット指定 AoE 系",
        "stack" => "中央集合マーカー。\n例：シェアダメージ、テラスト系",
        "cone" => "指定方向への扇形危険ゾーン。\n方向と扇の角度（90 度等）を別途設定。",
        _ => gimmick,
    };

    /// <summary>safe_zone（SafeZoneCalculation）編集ヘルパ。method 選択 + raw JSON params。</summary>
    private void DrawSafeZoneSubEditor(ActionDefinition action)
    {
        ImGui.Spacing();
        var hasZone = action.SafeZone is not null;
        if (ImGui.CollapsingHeader($"安置計算 (safe_zone){(hasZone ? "  ✓" : "")}##sz"))
        {
            ImGui.Indent(12f);

            if (!hasZone)
            {
                if (ImGui.Button("安置計算を追加##sz-add"))
                {
                    action.SafeZone = new SafeZoneCalculation { Method = "fixed" };
                    _dirty = true;
                }
                ImGui.TextDisabled("F4-F7 の 16 種プリセットから選んで世界座標を計算します。");
            }
            else
            {
                var sz = action.SafeZone!;
                var method = sz.Method;
                var mIdx = Array.IndexOf(SafeZoneMethods, method);
                if (mIdx < 0) mIdx = 0;
                var methodLabels = Localization.LocalizeAll(SafeZoneMethods, Localization.SafeZoneMethod);
                ImGui.SetNextItemWidth(360f * ImGuiHelpers.GlobalScale);
                if (ImGui.Combo("計算方式##sz-method", ref mIdx, methodLabels, methodLabels.Length))
                {
                    sz.Method = SafeZoneMethods[mIdx];
                    _dirty = true;
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(SafeZoneMethodTooltip(sz.Method));

                ImGui.TextDisabled("params (JSON):");
                var paramsBuf = SerializeParams(sz);
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputTextMultiline("##sz-params", ref paramsBuf, 4096,
                    new Vector2(-1, 80f * ImGuiHelpers.GlobalScale)))
                {
                    if (TryParseParams(paramsBuf, out var newParams, out var err))
                    {
                        sz.Params = newParams;
                        _szParseError = null;
                        _dirty = true;
                    }
                    else
                    {
                        _szParseError = err;
                    }
                }
                if (!string.IsNullOrEmpty(_szParseError))
                {
                    ImGui.TextColored(new Vector4(1f, 0.5f, 0.5f, 1f), $"JSON エラー: {_szParseError}");
                }
                if (ImGui.SmallButton("削除##sz-remove"))
                {
                    action.SafeZone = null;
                    _dirty = true;
                }
            }

            ImGui.Unindent(12f);
        }
    }

    private string? _szParseError;

    /// <summary>
    /// 集計イベントをフィルタする。「自分・PT のイベントを隠す」「status を隠す」の組合せ。
    /// PT 名は録画 meta から取得する。
    /// </summary>
    private IReadOnlyList<Recording.AggregatedEvent> ApplyEventFilters(Recording.AggregatedEvents agg)
    {
        if (!_hideSelfEvents && !_hideStatusEvents) return agg.Events;

        HashSet<string>? party = null;
        if (_hideSelfEvents)
        {
            var members = _recordingScanner.ListPartyMembers(_workingZone);
            if (members.Count > 0)
            {
                party = new HashSet<string>(members, StringComparer.OrdinalIgnoreCase);
            }
        }

        var result = new List<Recording.AggregatedEvent>(agg.Events.Count);
        foreach (var ev in agg.Events)
        {
            if (_hideStatusEvents && (ev.Key.Type == "status_gain" || ev.Key.Type == "status_lose" ||
                                       ev.Key.Type == "status_update"))
            {
                continue;
            }
            if (party is not null)
            {
                // status_gain / status_update / hp_change : Target が PT 内 → 隠す
                if (!string.IsNullOrEmpty(ev.Key.Target) && party.Contains(ev.Key.Target!))
                {
                    continue;
                }
                // cast_start 等：Source が PT 内 → 隠す
                if (!string.IsNullOrEmpty(ev.Key.Source) && party.Contains(ev.Key.Source!))
                {
                    continue;
                }
            }
            result.Add(ev);
        }
        return result;
    }

    private static string SafeZoneMethodTooltip(string method) => method switch
    {
        "fixed" => "params 例: { x: 100, y: 0, z: 100 }\n固定の世界座標を安置とする",
        "boss_relative" => "params 例: { offset: { x: 0, z: 15 } }\nボス位置からの相対オフセット（南に 15m など）",
        "marker_relative" => "params 例: { marker: \"A\" }\nフィールドマーカー（A/B/C/D/1/2/3/4）基準",
        "arena_center_relative" => "params 例: { offset: { x: 0, z: -15 } }\nアリーナ中心からの相対",
        "inverse_of_telegraph" => "params 例: { telegraph: { shape: \"fan\", angle_deg: 180 } }\n敵の AoE の反対側を安置とする（最も使う）",
        "party_member_relative" => "params 例: { role: \"tank\", index: 0, offset: { z: 5 } }\n指定ロールの PT メンバー基準",
        "find_actor_with_status" => "params 例: { status_id: 1234, offset: { z: 5 } }\n特定ステータスを持つ敵基準",
        "find_actor_without_status" => "params 例: { status_id: 1234 }\n特定ステータスを持たない敵基準",
        "find_actor_not_casting" => "params 例: { offset: { z: 0 } }\nキャストしていない敵基準",
        "find_actor_by_distance" => "params 例: { side: \"nearest\" }\n自分から最も近い／遠い敵基準",
        "midpoint" => "params 例: { actors: [\"敵A\", \"敵B\"] }\n2 アクターの中点",
        "between_actors" => "params 例: { actor_a: \"X\", actor_b: \"Y\", fraction: 0.5 }\n2 アクターを結ぶ線分上の指定割合の点",
        "line_perpendicular" => "params 例: { actors: [\"X\", \"Y\"], distance: 10 }\n2 点を結ぶ線への垂線方向",
        "telegraph_gap" => "params 例: { telegraphs: [...] }\n複数 AoE の隙間",
        "intersection" => "params 例: { calculations: [calc1, calc2] }\n複数計算の交差点（AND）",
        "stored_position" => "params 例: { name: \"slot1\" }\n以前 store_position で保存した位置（P4）",
        _ => method,
    };

    private static string SerializeParams(SafeZoneCalculation sz)
    {
        if (sz.Params is null || sz.Params.Count == 0)
        {
            return "{}";
        }
        try
        {
            return JsonSerializer.Serialize(sz.Params, new JsonSerializerOptions
            {
                WriteIndented = false,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        }
        catch
        {
            return "{}";
        }
    }

    private static bool TryParseParams(string text, out Dictionary<string, JsonElement>? result, out string? error)
    {
        result = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text) || text.Trim() == "{}")
        {
            return true;
        }
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "object でなければなりません";
                return false;
            }
            var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.Clone();
            }
            result = dict;
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
