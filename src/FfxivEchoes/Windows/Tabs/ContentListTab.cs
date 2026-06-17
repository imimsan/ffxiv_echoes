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
/// 録画ありゾーン + トリガー定義ゾーンの一覧（SPEC.md §7.1.1）。
/// </summary>
public sealed class ContentListTab : ITab
{
    public string Title => "コンテンツ一覧";
    public string Id => "content-list";

    private readonly TriggerStore _triggerStore;
    private readonly RecordingScanner _recordingScanner;
    private readonly TabContext _tabContext;

    private string _newZoneName = string.Empty;
    private string? _saveError;
    private string? _pendingDeleteZone;
    private string? _pendingDeleteRecordingsZone;
    private bool _shouldOpenDeleteRecordingsPopup;
    // BeginTable 内から OpenPopup を呼ぶと ID stack 不一致で開かないため、
    // テーブル外で開けるようフラグで遅延させる
    private bool _shouldOpenDeletePopup;

    public ContentListTab(TriggerStore triggerStore, RecordingScanner scanner, TabContext context)
    {
        _triggerStore = triggerStore;
        _recordingScanner = scanner;
        _tabContext = context;
    }

    public void Draw()
    {
        ImGui.TextDisabled($"トリガー定義 {_triggerStore.LoadedFileCount} 件 / 録画フォルダあり {_recordingScanner.ListZonesWithRecordings().Count} ゾーン");
        ImGui.Spacing();

        if (ImGui.Button("再読込"))
        {
            _triggerStore.Reload();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("（FileSystemWatcher で自動再読込もされます）");
        ImGui.SameLine(0, 24f * ImGuiHelpers.GlobalScale);
        if (ImGui.Button("新規ゾーン作成"))
        {
            _newZoneName = string.Empty;
            ImGui.OpenPopup("new-zone-popup");
        }
        DrawNewZonePopup();
        DrawDeleteConfirmPopup();
        DrawDeleteRecordingsPopup();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ゾーン名の和集合（トリガー定義あり ∪ 録画あり）
        var triggerZones = new HashSet<string>(_triggerStore.Snapshot().Keys, StringComparer.OrdinalIgnoreCase);
        var recordingZones = new HashSet<string>(_recordingScanner.ListZonesWithRecordings(), StringComparer.OrdinalIgnoreCase);
        var allZones = new HashSet<string>(triggerZones, StringComparer.OrdinalIgnoreCase);
        allZones.UnionWith(recordingZones);

        if (allZones.Count == 0)
        {
            ImGui.TextWrapped("コンテンツが見つかりません。上の「新規ゾーン作成」で作るか、" +
                "/echoes record on で録画してインスタンスに突入すると、ここに表示されます。");
            return;
        }

        if (ImGui.BeginTable("##content-list-table", 4,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("ゾーン", ImGuiTableColumnFlags.WidthStretch, 3.0f);
            ImGui.TableSetupColumn("トリガー数", ImGuiTableColumnFlags.WidthFixed, 90f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("録画", ImGuiTableColumnFlags.WidthFixed, 80f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 220f * ImGuiHelpers.GlobalScale);
            ImGui.TableHeadersRow();

            foreach (var zone in allZones.OrderBy(z => z, StringComparer.OrdinalIgnoreCase))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(zone);

                ImGui.TableNextColumn();
                var triggerFile = _triggerStore.GetByZone(zone);
                if (triggerFile is not null)
                {
                    var enabled = triggerFile.Triggers.Count(t => t.Enabled);
                    ImGui.TextUnformatted($"{enabled}/{triggerFile.Triggers.Count}");
                }
                else
                {
                    ImGui.TextDisabled("未作成");
                }

                ImGui.TableNextColumn();
                var recordings = _recordingScanner.ListRecordings(zone);
                if (recordings.Count > 0)
                {
                    ImGui.TextUnformatted($"{recordings.Count} 戦闘");
                }
                else
                {
                    ImGui.TextDisabled("—");
                }

                ImGui.TableNextColumn();
                var editButtonLabel = triggerFile is null && recordings.Count > 0
                    ? $"録画から作る##{zone}"
                    : $"編集##{zone}";
                if (ImGui.Button(editButtonLabel))
                {
                    _tabContext.SelectedZone = zone;
                    _tabContext.PendingCreateFromRecording = triggerFile is null && recordings.Count > 0;
                    _tabContext.PendingFocusTab = "trigger-editor";
                }
                ImGui.SameLine();
                if (triggerFile is not null)
                {
                    if (ImGui.Button($"削除##{zone}"))
                    {
                        _pendingDeleteZone = zone;
                        _shouldOpenDeletePopup = true;
                    }
                }
                if (recordings.Count > 0)
                {
                    ImGui.SameLine();
                    if (ImGui.Button($"録画削除##{zone}"))
                    {
                        _pendingDeleteRecordingsZone = zone;
                        _deleteAlsoClearMechanics = true;
                        _deleteAlsoClearTriggers = true;
                        _deleteAlsoClearArenaShape = true;
                        _deleteAlsoClearNotes = true;
                        _shouldOpenDeleteRecordingsPopup = true;
                    }
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip($"このゾーンの録画 {recordings.Count} 件をすべて削除する。\n" +
                                          "学習データもリセットされる（次戦闘から再学習）。");
                    }
                }
            }

            ImGui.EndTable();
        }

        // BeginTable の外で OpenPopup を呼ぶ
        if (_shouldOpenDeletePopup)
        {
            _shouldOpenDeletePopup = false;
            ImGui.OpenPopup("delete-zone-popup");
        }
        if (_shouldOpenDeleteRecordingsPopup)
        {
            _shouldOpenDeleteRecordingsPopup = false;
            ImGui.OpenPopup("delete-recordings-popup");
        }
    }

    private void DrawNewZonePopup()
    {
        ImGui.SetNextWindowSize(new Vector2(380f * ImGuiHelpers.GlobalScale, 0f));
        if (!ImGui.BeginPopupModal("new-zone-popup", ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.TextWrapped("新規ゾーン名（例：「武神の闘技場」）。" +
                          "ゲーム内のコンテンツ名と完全一致させるとそのコンテンツに入った時に自動でアクティブになる。");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##new-zone-name", ref _newZoneName, 128);

        if (!string.IsNullOrEmpty(_saveError))
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), _saveError);
        }

        ImGui.Spacing();
        var trimmed = _newZoneName.Trim();
        var canCreate = trimmed.Length > 0 && _triggerStore.GetByZone(trimmed) is null;

        if (!canCreate)
        {
            ImGui.BeginDisabled();
        }
        if (ImGui.Button("作成", new Vector2(120f * ImGuiHelpers.GlobalScale, 0f)))
        {
            try
            {
                var newFile = new TriggerFile { Zone = trimmed };
                newFile.AutoSettings.EnableTriggers = true;
                _triggerStore.SaveZone(trimmed, newFile);
                _saveError = null;
                _tabContext.SelectedZone = trimmed;
                _tabContext.PendingFocusTab = "trigger-editor";
                ImGui.CloseCurrentPopup();
            }
            catch (Exception ex)
            {
                _saveError = $"作成に失敗：{ex.Message}";
            }
        }
        if (!canCreate)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        if (ImGui.Button("キャンセル", new Vector2(120f * ImGuiHelpers.GlobalScale, 0f)))
        {
            _saveError = null;
            ImGui.CloseCurrentPopup();
        }

        if (trimmed.Length > 0 && _triggerStore.GetByZone(trimmed) is not null)
        {
            ImGui.TextColored(new Vector4(1f, 0.7f, 0.4f, 1f),
                "そのゾーンは既に存在します。コンテンツ一覧から「編集」してください。");
        }

        ImGui.EndPopup();
    }

    private bool _deleteAlsoClearMechanics;
    private bool _deleteAlsoClearTriggers;
    private bool _deleteAlsoClearArenaShape;
    private bool _deleteAlsoClearNotes;

    private void DrawDeleteRecordingsPopup()
    {
        ImGui.SetNextWindowSize(new Vector2(440f * ImGuiHelpers.GlobalScale, 0f));
        if (!ImGui.BeginPopupModal("delete-recordings-popup", ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        var zone = _pendingDeleteRecordingsZone ?? "";
        var recordings = _recordingScanner.ListRecordings(zone);
        ImGui.TextWrapped(
            $"ゾーン \"{zone}\" の録画 {recordings.Count} 件を削除します。\n" +
            "下のチェックで関連データも一緒にクリア可能：");
        ImGui.Spacing();

        ImGui.Checkbox("攻略登録のメカニクスも全削除##del-mech", ref _deleteAlsoClearMechanics);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(
            "「攻略登録」タブのメカニクス（コキュートス／パラデイグマ等）を全件削除する。\n" +
            "録画から作ったエントリも、テンプレートから作ったエントリも、手動で作ったエントリも全部消える。");

        ImGui.Checkbox("トリガー一覧も全削除##del-trig", ref _deleteAlsoClearTriggers);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(
            "「トリガー一覧」タブの個別トリガーを全件削除する。");

        ImGui.Checkbox("アリーナ形状・寸法・中心もリセット##del-arena", ref _deleteAlsoClearArenaShape);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(
            "プロファイルのアリーナ形状・寸法・中心座標を未設定に戻す。\n" +
            "次回エディタを開いたときに録画から再推定される（録画も消すなら再推定はできない）。");

        ImGui.Checkbox("ノートも全削除##del-notes", ref _deleteAlsoClearNotes);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(
            "「ノート」タブのタイムラインメモを全件削除する。\n" +
            "録画キャストから一括追加したメモを最初から作り直すときに使う。");

        ImGui.Spacing();
        if (ImGui.Button("削除を実行", new Vector2(140f * ImGuiHelpers.GlobalScale, 0f)))
        {
            try
            {
                _recordingScanner.DeleteAllRecordings(zone);

                var existing = _triggerStore.GetByZone(zone);
                var file = existing is null ? null : Clone(existing);
                if (file is not null && (_deleteAlsoClearMechanics || _deleteAlsoClearTriggers || _deleteAlsoClearArenaShape || _deleteAlsoClearNotes))
                {
                    if (_deleteAlsoClearMechanics)
                    {
                        foreach (var profile in file.StrategyProfiles)
                        {
                            profile.Mechanics.Clear();
                            profile.PhaseArenaShapes.Clear();
                        }
                    }
                    if (_deleteAlsoClearTriggers)
                    {
                        file.Triggers.Clear();
                    }
                    if (_deleteAlsoClearNotes)
                    {
                        file.Notes.Clear();
                    }
                    if (_deleteAlsoClearArenaShape)
                    {
                        foreach (var profile in file.StrategyProfiles)
                        {
                            profile.ArenaShape = "circle";
                            profile.ArenaRadius = null;
                            profile.ArenaWidth = null;
                            profile.ArenaDepth = null;
                            profile.ArenaCenterX = null;
                            profile.ArenaCenterZ = null;
                        }
                    }
                    _triggerStore.SaveZone(zone, file);
                }

                _pendingDeleteRecordingsZone = null;
                _deleteAlsoClearMechanics = false;
                _deleteAlsoClearTriggers = false;
                _deleteAlsoClearArenaShape = false;
                _deleteAlsoClearNotes = false;
                ImGui.CloseCurrentPopup();
            }
            catch (Exception ex)
            {
                _saveError = $"削除に失敗：{ex.Message}";
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("キャンセル", new Vector2(120f * ImGuiHelpers.GlobalScale, 0f)))
        {
            _pendingDeleteRecordingsZone = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void DrawDeleteConfirmPopup()
    {
        ImGui.SetNextWindowSize(new Vector2(380f * ImGuiHelpers.GlobalScale, 0f));
        if (!ImGui.BeginPopupModal("delete-zone-popup", ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        var zone = _pendingDeleteZone ?? "";
        ImGui.TextWrapped($"ゾーン \"{zone}\" のトリガー定義ファイルを削除します。録画ファイルは残ります。続行しますか？");
        ImGui.Spacing();

        if (ImGui.Button("削除する", new Vector2(120f * ImGuiHelpers.GlobalScale, 0f)))
        {
            try
            {
                _triggerStore.DeleteZone(zone);
                if (string.Equals(_tabContext.SelectedZone, zone, StringComparison.OrdinalIgnoreCase))
                {
                    _tabContext.SelectedZone = null;
                    _tabContext.PendingCreateFromRecording = false;
                }
                _pendingDeleteZone = null;
                ImGui.CloseCurrentPopup();
            }
            catch (Exception ex)
            {
                _saveError = $"削除に失敗：{ex.Message}";
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("キャンセル", new Vector2(120f * ImGuiHelpers.GlobalScale, 0f)))
        {
            _pendingDeleteZone = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private static TriggerFile Clone(TriggerFile src)
    {
        var json = JsonSerializer.Serialize(src);
        return JsonSerializer.Deserialize<TriggerFile>(json)!;
    }
}
