namespace FfxivEchoes.Windows.Tabs;

public sealed class TriggerEditorTab : PlaceholderTab
{
    public override string Title => "トリガー編集";
    public override string Id => "trigger-editor";
    protected override string PlannedFor => "M8 / F1 / F12";
    protected override string Description =>
        "選択したコンテンツのトリガーを編集する画面。タイムラインビューと集計ビューを切り替え、" +
        "観測されたイベントから設定済み／未設定／無視済みを管理できる予定です（SPEC.md §7.2 参照）。";
}
