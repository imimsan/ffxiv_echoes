namespace FfxivEchoes.Windows.Tabs;

public sealed class ProfileTab : PlaceholderTab
{
    public override string Title => "プロファイル";
    public override string Id => "profile";
    protected override string PlannedFor => "F10";
    protected override string Description =>
        "ジョブ別・用途別のプロファイル切替。各プロファイルで有効化するトリガー ID 群を" +
        "コンテンツごとに管理し、ジョブ切替に追従して自動切替する予定です（SPEC.md §3.3 参照）。";
}
