namespace FfxivEchoes.Windows.Tabs;

public sealed class ContentListTab : PlaceholderTab
{
    public override string Title => "コンテンツ一覧";
    public override string Id => "content-list";
    protected override string PlannedFor => "M8";
    protected override string Description =>
        "録画されたコンテンツとトリガー定義の一覧をここに表示します。" +
        "コンテンツごとの自動記録 ON/OFF、最新の戦闘ログ、トリガー数などが見られる予定です。";
}
