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

    private string? _editingTriggerId;
    private TriggerFile? _workingCopy;
    private string _workingZone = string.Empty;
    private bool _dirty;
    private readonly TimelineRenderer _timelineRenderer = new();
    private bool _aggregateAsTimeline = true;
    private bool _hideSelfEvents = true;
    private bool _hideStatusEvents = false;

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
                case "arena_view":
                {
                    // gimmick タイプ
                    var gimmick = action.Gimmick ?? "outer_ring";
                    var gIdx = Array.IndexOf(ArenaViewGimmicks, gimmick);
                    if (gIdx < 0) gIdx = 0;
                    ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
                    if (ImGui.Combo("gimmick##action-gimmick", ref gIdx, ArenaViewGimmicks, ArenaViewGimmicks.Length))
                    {
                        action.Gimmick = ArenaViewGimmicks[gIdx];
                        _dirty = true;
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(GimmickTooltip(action.Gimmick ?? "outer_ring"));
                    // callout
                    var callout = action.Callout ?? string.Empty;
                    ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputText("callout##action-callout", ref callout, 128))
                    {
                        action.Callout = callout;
                        _dirty = true;
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("ミニマップ下部に表示されるテキスト（例：「中央安置」）");
                    // duration
                    var dur = (float)(action.Duration ?? 5.0);
                    ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputFloat("duration (s)##action-duration", ref dur))
                    {
                        action.Duration = dur <= 0 ? null : dur;
                        _dirty = true;
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("表示秒数。キャスト時間 + α が目安");
                    // arena_radius
                    var ar = (float)(action.ArenaRadius ?? 20.0);
                    ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputFloat("arena_radius (m)##action-ar", ref ar))
                    {
                        action.ArenaRadius = ar <= 0 ? null : ar;
                        _dirty = true;
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("プレイヤー位置プロット用のアリーナ実半径（メートル）。多くの極/絶は 18-25m");
                    // cone のときだけ direction + fan_deg
                    if (action.Gimmick == "cone")
                    {
                        var dir = action.Direction ?? "N";
                        var dIdx = Array.IndexOf(ArenaViewDirections, dir);
                        if (dIdx < 0) dIdx = 0;
                        ImGui.SetNextItemWidth(80f * ImGuiHelpers.GlobalScale);
                        if (ImGui.Combo("direction##action-dir", ref dIdx, ArenaViewDirections, ArenaViewDirections.Length))
                        {
                            action.Direction = ArenaViewDirections[dIdx];
                            _dirty = true;
                        }
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip("コーンが向く方位（北上で N=北、E=東 等）");
                        var fan = (float)(action.FanDeg ?? 90.0);
                        ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
                        if (ImGui.InputFloat("fan_deg##action-fan", ref fan))
                        {
                            action.FanDeg = fan <= 0 ? null : fan;
                            _dirty = true;
                        }
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip("扇形の角度（度数法）。90 で 90 度の扇");
                    }
                    DrawSafeZoneSubEditor(action);
                    break;
                }
                case "direction_call":
                {
                    var fmt = action.Format ?? "cardinal_jp";
                    var fIdx = Array.IndexOf(DirectionFormats, fmt);
                    if (fIdx < 0) fIdx = 0;
                    ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
                    if (ImGui.Combo("format##action-fmt", ref fIdx, DirectionFormats, DirectionFormats.Length))
                    {
                        action.Format = DirectionFormats[fIdx];
                        _dirty = true;
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("読み上げ表記：cardinal=「north」、cardinal_jp=「北」、clock=「12時」、relative_jp=「左前」");

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
                    ImGui.SetNextItemWidth(160f * ImGuiHelpers.GlobalScale);
                    if (ImGui.Combo("shape##action-shape", ref sIdx, FieldShapes, FieldShapes.Length))
                    {
                        action.Shape = FieldShapes[sIdx];
                        _dirty = true;
                    }
                    var rad = (float)(action.Radius ?? 3.0);
                    ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputFloat("radius (m)##action-fm-rad", ref rad)) { action.Radius = rad <= 0 ? null : rad; _dirty = true; }
                    var durFm = (float)(action.Duration ?? 5.0);
                    ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputFloat("duration (s)##action-dur-fm", ref durFm)) { action.Duration = durFm <= 0 ? null : durFm; _dirty = true; }
                    var colorFm = action.Color ?? "#00FF00";
                    ImGui.SetNextItemWidth(140f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputText("color (#RRGGBB)##action-color-fm", ref colorFm, 16)) { action.Color = colorFm; _dirty = true; }
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

            foreach (var ev in filteredEvents)
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

        ImGui.Spacing();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("予測アドバンス警告:");
        ImGui.SameLine();
        var warnEnabled = _workingCopy.AutoSettings.PredictAdvanceWarningSec is > 0;
        if (ImGui.Checkbox("##predict-warn-enable", ref warnEnabled))
        {
            _workingCopy.AutoSettings.PredictAdvanceWarningSec = warnEnabled ? 5.0 : null;
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
            var warnSec = (float)(_workingCopy.AutoSettings.PredictAdvanceWarningSec ?? 5.0);
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
        ImGui.TextWrapped("時刻指定で「ここで軽減」「ここで LB」などを書いておくと、" +
            "ライブタイムラインに表示されます。advance_warning_sec を設定すると " +
            "その秒数前に TTS / オーバーレイで先行通知します。");
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
        ImGui.Spacing();

        if (_workingCopy.Notes.Count == 0)
        {
            ImGui.TextDisabled("ノートがありません。「新規ノート」で追加してください。");
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
                var time = (float)note.Time;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputFloat("##time", ref time, 1.0f, 5.0f, "%.1f")) { note.Time = time; _dirty = true; }

                ImGui.TableNextColumn();
                var label = note.Label;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("##label", ref label, 128)) { note.Label = label; _dirty = true; }

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

    /// <summary>arena_view の gimmick タイプごとの説明文。</summary>
    private static string GimmickTooltip(string gimmick) => gimmick switch
    {
        "outer_ring" => "外周が危険、中央に安置丸を表示（無の肥大タイプ）",
        "inner_circle" => "中央が危険、外周は通常（円形 AoE）",
        "scatter" => "4 方向（N/E/S/W）にマーカーを表示（散開）",
        "stack" => "中央に集合マーカー",
        "cone" => "指定方向への扇形を危険ゾーンとして表示。direction + fan_deg を設定",
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
                ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
                if (ImGui.Combo("method##sz-method", ref mIdx, SafeZoneMethods, SafeZoneMethods.Length))
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
        "fixed" => "params: { x, y, z } の固定座標",
        "boss_relative" => "params: { offset: {x,y,z} } ボスからの相対",
        "marker_relative" => "params: { marker: \"A\"|\"B\"|... } フィールドマーカー基準",
        "arena_center_relative" => "params: { offset: {x,y,z} } アリーナ中心からの相対",
        "inverse_of_telegraph" => "params: { telegraph: {shape, ...} } AoE の反対側",
        "party_member_relative" => "params: { role: \"tank\"|..., index: 0 } PT メンバー基準",
        "find_actor_with_status" => "params: { status_id, offset } 特定 status を持つ敵基準",
        "find_actor_without_status" => "params: { status_id, offset } 特定 status を持たない敵基準",
        "find_actor_not_casting" => "params: { offset } キャストしていない敵基準",
        "find_actor_by_distance" => "params: { side: \"nearest\"|\"farthest\" }",
        "midpoint" => "params: { actors: [name, name] } 2 アクターの中点",
        "between_actors" => "params: { actor_a, actor_b, fraction } 2 アクター間のうち指定割合",
        "line_perpendicular" => "params: { actors: [a,b], distance } 2 点を結ぶ線への垂線",
        "telegraph_gap" => "params: { telegraphs: [...] } 複数 AoE の隙間",
        "intersection" => "params: { calculations: [calc1, calc2, ...] } 複数の計算結果の AND",
        "stored_position" => "params: { name } P4: 以前 store_position で保存した位置",
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
