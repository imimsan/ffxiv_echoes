using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FfxivEchoes.Profiles;

/// <summary>
/// プロファイル定義。
/// </summary>
public sealed class Profile
{
    /// <summary>内部 ID。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "default";

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = "デフォルト";

    /// <summary>job 略称。空ならジョブ自動切替の対象外。</summary>
    [JsonPropertyName("auto_switch_jobs")]
    public List<string> AutoSwitchJobs { get; set; } = new();

    /// <summary>
    /// zone 名ごとの有効トリガー ID リスト。
    /// キーなし/従来の空リストは全有効、custom_trigger_zones に含まれる zone の空リストは全無効。
    /// </summary>
    [JsonPropertyName("active_triggers")]
    public Dictionary<string, List<string>> ActiveTriggers { get; set; } = new();

    /// <summary>
    /// active_triggers を明示的な有効 ID リストとして扱う zone。
    /// 既存 JSON 互換のため、未設定なら従来の active_triggers の意味を維持する。
    /// </summary>
    [JsonPropertyName("custom_trigger_zones")]
    public List<string> CustomTriggerZones { get; set; } = new();

    /// <summary>このプロファイルでトリガー id が有効か判定する。</summary>
    public bool IsTriggerActive(string zone, string triggerId)
    {
        if (!ActiveTriggers.TryGetValue(zone, out var list))
        {
            return true;
        }

        if (list.Count == 0)
        {
            return !UsesCustomTriggerSelection(zone);
        }

        foreach (var id in list)
        {
            if (string.Equals(id, triggerId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public bool UsesCustomTriggerSelection(string zone)
    {
        foreach (var customZone in CustomTriggerZones)
        {
            if (string.Equals(customZone, zone, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return ActiveTriggers.TryGetValue(zone, out var list) && list.Count > 0;
    }

    public void SetCustomTriggerSelection(string zone, bool enabled)
    {
        var existingIndex = CustomTriggerZones.FindIndex(z => string.Equals(z, zone, StringComparison.OrdinalIgnoreCase));
        if (enabled)
        {
            if (existingIndex < 0)
            {
                CustomTriggerZones.Add(zone);
            }
        }
        else
        {
            if (existingIndex >= 0)
            {
                CustomTriggerZones.RemoveAt(existingIndex);
            }
        }
    }
}
