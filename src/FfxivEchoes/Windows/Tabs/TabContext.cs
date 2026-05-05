namespace FfxivEchoes.Windows.Tabs;

/// <summary>
/// タブ間で共有する状態（現在編集中のゾーンなど）。
/// </summary>
public sealed class TabContext
{
    public string? SelectedZone { get; set; }

    /// <summary>次フレームでフォーカスしたいタブ ID（MainWindow が処理）。</summary>
    public string? PendingFocusTab { get; set; }
}
