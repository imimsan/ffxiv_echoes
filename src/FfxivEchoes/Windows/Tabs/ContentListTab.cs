using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using FfxivEchoes.Recording;
using FfxivEchoes.Triggers;

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
            ImGui.TextWrapped("コンテンツが見つかりません。triggers/ にトリガー定義ファイル（*.json）を置くか、" +
                "/echoes record on で録画してインスタンスに突入すると、ここに表示されます。");
            return;
        }

        if (ImGui.BeginTable("##content-list-table", 4,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("ゾーン", ImGuiTableColumnFlags.WidthStretch, 3.0f);
            ImGui.TableSetupColumn("トリガー数", ImGuiTableColumnFlags.WidthFixed, 90f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("録画", ImGuiTableColumnFlags.WidthFixed, 80f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 140f * ImGuiHelpers.GlobalScale);
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
                    ImGui.TextDisabled("—");
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
                if (ImGui.Button($"編集##{zone}"))
                {
                    _tabContext.SelectedZone = zone;
                    _tabContext.PendingFocusTab = "trigger-editor";
                }
            }

            ImGui.EndTable();
        }
    }
}
