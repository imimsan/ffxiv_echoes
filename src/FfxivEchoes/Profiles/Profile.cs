using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FfxivEchoes.Profiles;

/// <summary>
/// プロファイル定義（SPEC.md §3.3）。
/// </summary>
/// <remarks>
/// 複数のトリガーがコンテンツ単位で存在する中、プロファイルは「どれを有効化するか」
/// を ID リストで管理する。same TriggerFile を異なるプロファイルから共有できる。
/// </remarks>
public sealed class Profile
{
    /// <summary>内部 ID（ファイル名にも使う）。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "default";

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = "デフォルト";

    /// <summary>job 略称（"PLD" など）。空ならジョブ自動切替の対象外。</summary>
    [JsonPropertyName("auto_switch_jobs")]
    public List<string> AutoSwitchJobs { get; set; } = new();

    /// <summary>zone 名 → 有効化したいトリガー ID のリスト。
    /// 空のリストは「全トリガー有効」を意味する。
    /// 該当 zone のキーが存在しなければ「全トリガー有効」を意味する。</summary>
    [JsonPropertyName("active_triggers")]
    public Dictionary<string, List<string>> ActiveTriggers { get; set; } = new();

    /// <summary>このプロファイルでトリガー id が有効か判定する。</summary>
    public bool IsTriggerActive(string zone, string triggerId)
    {
        if (!ActiveTriggers.TryGetValue(zone, out var list))
        {
            return true; // ゾーンの設定がない = 全トリガー有効（デフォルト）
        }
        if (list.Count == 0)
        {
            return true; // 空リスト = 全トリガー有効
        }
        foreach (var id in list)
        {
            if (string.Equals(id, triggerId, System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
