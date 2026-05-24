using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using FfxivEchoes.Profiles;
using FfxivEchoes.Triggers;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Windows.Tabs;

public sealed class ProfileTab : ITab
{
    public string Title => "プロファイル";
    public string Id => "profile";

    private readonly ProfileStore _store;
    private readonly TriggerStore _triggerStore;
    private readonly Configuration _configuration;

    private string _newProfileName = string.Empty;
    private string _newProfileDisplayName = string.Empty;
    private string? _selectedProfile;

    public ProfileTab(ProfileStore store, TriggerStore triggerStore, Configuration configuration)
    {
        _store = store;
        _triggerStore = triggerStore;
        _configuration = configuration;
    }

    public void Draw()
    {
        ImGui.TextWrapped("プロファイルは「どのトリガーを有効化するか」をゾーン単位で管理します。" +
            "ジョブ別やロール別の使い分けに利用できます。");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawProfileList();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawSelectedProfileEditor();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawNewProfileSection();
    }

    private void DrawProfileList()
    {
        ImGui.TextUnformatted($"アクティブ：{_configuration.ActiveProfile}");
        ImGui.Spacing();

        if (ImGui.BeginTable("##profile-list", 4,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("名前", ImGuiTableColumnFlags.WidthStretch, 1.5f);
            ImGui.TableSetupColumn("表示名", ImGuiTableColumnFlags.WidthStretch, 2.5f);
            ImGui.TableSetupColumn("自動切替ジョブ", ImGuiTableColumnFlags.WidthStretch, 2.0f);
            ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 200f * ImGuiHelpers.GlobalScale);
            ImGui.TableHeadersRow();

            foreach (var (name, p) in _store.All)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(name);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(p.DisplayName);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(p.AutoSwitchJobs.Count == 0 ? "—" : string.Join(", ", p.AutoSwitchJobs));
                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"{(name == _configuration.ActiveProfile ? "●" : "○")} 選択##sel-{name}"))
                {
                    _configuration.ActiveProfile = name;
                    _configuration.Save();
                }
                ImGui.SameLine();
                if (ImGui.SmallButton($"編集##edit-{name}"))
                {
                    _selectedProfile = name;
                }
                if (name != ProfileStore.DefaultProfileName)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"削除##del-{name}"))
                    {
                        _store.Delete(name);
                        if (_configuration.ActiveProfile == name)
                        {
                            _configuration.ActiveProfile = ProfileStore.DefaultProfileName;
                            _configuration.Save();
                        }
                    }
                }
            }
            ImGui.EndTable();
        }
    }

    private void DrawSelectedProfileEditor()
    {
        ImGui.TextUnformatted("プロファイル編集");
        if (_selectedProfile is null)
        {
            ImGui.TextDisabled("  上で「編集」ボタンを押してプロファイルを選択");
            return;
        }
        var profile = _store.Get(_selectedProfile);
        if (profile is null)
        {
            ImGui.TextDisabled("  選択されたプロファイルが見つかりません");
            return;
        }

        var displayName = profile.DisplayName;
        ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("表示名##profile-display"u8, ref displayName, 64))
        {
            profile.DisplayName = displayName;
        }

        var jobsCsv = string.Join(",", profile.AutoSwitchJobs);
        ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("自動切替ジョブ（カンマ区切り）##profile-jobs"u8, ref jobsCsv, 256))
        {
            profile.AutoSwitchJobs = ParseJobs(jobsCsv);
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("有効トリガー（ゾーン別）");
        ImGui.TextDisabled("  通常は全有効です。個別選択を使うと、チェック ON のトリガーだけ有効になります。");

        ImGui.TextDisabled("  全有効: zone 未設定/従来の空リストは全有効です。個別選択中: チェック ON のIDだけ有効です。");

        var snapshot = _triggerStore.Snapshot();
        if (snapshot.Count == 0)
        {
            ImGui.TextDisabled("  ロード済みのトリガー定義がありません");
        }
        else
        {
            foreach (var (zone, file) in snapshot)
            {
                if (ImGui.CollapsingHeader($"{zone}（{file.Triggers.Count}）"))
                {
                    var customSelection = profile.UsesCustomTriggerSelection(zone);
                    if (ImGui.Checkbox($"個別選択を使う##custom-{zone}", ref customSelection))
                    {
                        profile.SetCustomTriggerSelection(zone, customSelection);
                        if (customSelection)
                        {
                            profile.ActiveTriggers[zone] = new List<string>(AllTriggerIds(file.Triggers));
                        }
                        else
                        {
                            profile.ActiveTriggers.Remove(zone);
                        }
                    }

                    ImGui.TextDisabled(customSelection
                        ? "  個別選択中: チェック ON のトリガーだけ有効です。空なら全無効です。"
                        : "  全有効: この zone の全トリガーが有効です。");

                    var list = profile.ActiveTriggers.TryGetValue(zone, out var ids)
                        ? new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase)
                        : new HashSet<string>(AllTriggerIds(file.Triggers), StringComparer.OrdinalIgnoreCase);

                    foreach (var t in file.Triggers)
                    {
                        var active = customSelection ? list.Contains(t.Id) : true;
                        if (ImGui.Checkbox($"{t.Id}{(t.Name is not null ? $" — {t.Name}" : string.Empty)}##{zone}-{t.Id}", ref active))
                        {
                            if (!customSelection)
                            {
                                customSelection = true;
                                profile.SetCustomTriggerSelection(zone, true);
                                list = new HashSet<string>(AllTriggerIds(file.Triggers), StringComparer.OrdinalIgnoreCase);
                            }

                            if (active) list.Add(t.Id);
                            else list.Remove(t.Id);
                            profile.ActiveTriggers[zone] = new List<string>(list);
                        }
                    }

                    if (list is not null && list.Count == file.Triggers.Count)
                    {
                        // 全 ON のときは ActiveTriggers のキーを消す（= 全有効のセマンティクス）
                        profile.ActiveTriggers.Remove(zone);
                    }
                }
            }
        }

        ImGui.Spacing();
        if (ImGui.Button("保存##profile-save"))
        {
            _store.Save(profile);
        }
    }

    private void DrawNewProfileSection()
    {
        ImGui.TextUnformatted("新規プロファイル");
        ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
        ImGui.InputText("ID##new-profile-name"u8, ref _newProfileName, 32);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
        ImGui.InputText("表示名##new-profile-display"u8, ref _newProfileDisplayName, 64);
        ImGui.SameLine();
        if (ImGui.Button("作成##new-profile"))
        {
            if (!string.IsNullOrWhiteSpace(_newProfileName))
            {
                var profile = new Profile
                {
                    Name = _newProfileName.Trim(),
                    DisplayName = string.IsNullOrWhiteSpace(_newProfileDisplayName)
                        ? _newProfileName.Trim()
                        : _newProfileDisplayName.Trim(),
                };
                _store.Save(profile);
                _newProfileName = string.Empty;
                _newProfileDisplayName = string.Empty;
            }
        }
    }

    private static IEnumerable<string> AllTriggerIds(IEnumerable<TriggerDefinition> triggers)
    {
        foreach (var trigger in triggers)
        {
            yield return trigger.Id;
        }
    }

    private static List<string> ParseJobs(string csv)
    {
        var list = new List<string>();
        foreach (var token in csv.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token.Trim();
            if (!string.IsNullOrEmpty(t))
            {
                list.Add(t);
            }
        }
        return list;
    }
}
