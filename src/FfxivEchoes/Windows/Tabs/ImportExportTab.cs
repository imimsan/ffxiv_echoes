using System;
using System.Diagnostics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using FfxivEchoes.Triggers;

namespace FfxivEchoes.Windows.Tabs;

public sealed class ImportExportTab : ITab
{
    public string Title => "入出力";
    public string Id => "import-export";

    private readonly TriggerStore _triggerStore;
    private readonly TriggerExportImport _exportImport;

    public ImportExportTab(TriggerStore triggerStore, TriggerExportImport exportImport)
    {
        _triggerStore = triggerStore;
        _exportImport = exportImport;
    }

    public void Draw()
    {
        ImGui.TextWrapped("ゾーン単位のトリガー定義を JSON ファイルとしてエクスポート / インポートします。" +
            "ファイルは ConfigDirectory 配下の exports/ と imports/ で扱います。");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawExportPanel();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawImportPanel();
    }

    private void DrawExportPanel()
    {
        ImGui.TextUnformatted("エクスポート");
        ImGui.TextDisabled($"  → {_exportImport.ExportsDirectory}");
        ImGui.SameLine();
        if (ImGui.SmallButton("フォルダを開く##open-exports"))
        {
            OpenFolder(_exportImport.ExportsDirectory);
        }
        ImGui.Spacing();

        var snapshot = _triggerStore.Snapshot();
        if (snapshot.Count == 0)
        {
            ImGui.TextDisabled("  エクスポートするゾーンがありません。");
            return;
        }

        if (ImGui.BeginTable("##export-table", 3,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("ゾーン", ImGuiTableColumnFlags.WidthStretch, 3.0f);
            ImGui.TableSetupColumn("トリガー数", ImGuiTableColumnFlags.WidthFixed, 90f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 120f * ImGuiHelpers.GlobalScale);
            ImGui.TableHeadersRow();

            foreach (var (zone, file) in snapshot)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(zone);
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{file.Triggers.Count}");
                ImGui.TableNextColumn();
                if (ImGui.Button($"エクスポート##exp-{zone}"))
                {
                    try
                    {
                        _exportImport.Export(zone);
                    }
                    catch (Exception ex)
                    {
                        _lastError = ex.Message;
                        ImGui.OpenPopup("export-error");
                    }
                }
            }
            ImGui.EndTable();
        }

        if (ImGui.BeginPopupModal("export-error", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped(_lastError ?? "エクスポートに失敗しました。");
            if (ImGui.Button("OK")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

    private string? _lastError;
    private ImportCandidate? _pendingImport;

    private void DrawImportPanel()
    {
        ImGui.TextUnformatted("インポート");
        ImGui.TextDisabled($"  ← {_exportImport.ImportsDirectory}");
        ImGui.SameLine();
        if (ImGui.SmallButton("フォルダを開く##open-imports"))
        {
            OpenFolder(_exportImport.ImportsDirectory);
        }
        ImGui.Spacing();
        ImGui.TextDisabled("  imports/ フォルダに JSON ファイルを置くと候補として表示されます。");
        ImGui.Spacing();

        var candidates = _exportImport.ListImports();
        if (candidates.Count == 0)
        {
            ImGui.TextDisabled("  候補ファイルがありません。");
            return;
        }

        if (ImGui.BeginTable("##import-table", 4,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("ファイル", ImGuiTableColumnFlags.WidthStretch, 3.0f);
            ImGui.TableSetupColumn("ゾーン", ImGuiTableColumnFlags.WidthStretch, 2.0f);
            ImGui.TableSetupColumn("既存衝突", ImGuiTableColumnFlags.WidthFixed, 80f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 120f * ImGuiHelpers.GlobalScale);
            ImGui.TableHeadersRow();

            foreach (var c in candidates)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(System.IO.Path.GetFileName(c.Path));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(c.File.Zone);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(c.HasConflict ? $"あり ({c.ExistingTriggerCount})" : "なし");
                ImGui.TableNextColumn();
                if (ImGui.Button($"インポート##imp-{c.Path}"))
                {
                    _pendingImport = c;
                    if (c.HasConflict)
                    {
                        ImGui.OpenPopup("import-conflict");
                    }
                    else
                    {
                        try
                        {
                            _exportImport.Import(c, ImportConflictResolution.Overwrite);
                        }
                        catch (Exception ex)
                        {
                            _lastError = ex.Message;
                            ImGui.OpenPopup("import-error");
                        }
                        _pendingImport = null;
                    }
                }
            }
            ImGui.EndTable();
        }

        if (ImGui.BeginPopupModal("import-conflict", ImGuiWindowFlags.AlwaysAutoResize))
        {
            var c = _pendingImport;
            if (c is not null)
            {
                ImGui.TextWrapped($"ゾーン \"{c.File.Zone}\" には既にトリガー定義があります" +
                    $"（既存 {c.ExistingTriggerCount} 件）。どう処理しますか？");
                ImGui.Spacing();
                if (ImGui.Button("上書き"))
                {
                    _exportImport.Import(c, ImportConflictResolution.Overwrite);
                    _pendingImport = null;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button("マージ（ID 重複なし追加）"))
                {
                    _exportImport.Import(c, ImportConflictResolution.Merge);
                    _pendingImport = null;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button("スキップ"))
                {
                    _pendingImport = null;
                    ImGui.CloseCurrentPopup();
                }
            }
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopupModal("import-error", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped(_lastError ?? "インポートに失敗しました。");
            if (ImGui.Button("OK")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            System.IO.Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch
        {
            // フォルダオープン失敗は黙って無視
        }
    }
}
