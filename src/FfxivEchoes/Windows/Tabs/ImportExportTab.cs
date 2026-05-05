namespace FfxivEchoes.Windows.Tabs;

public sealed class ImportExportTab : PlaceholderTab
{
    public override string Title => "入出力";
    public override string Id => "import-export";
    protected override string PlannedFor => "F11";
    protected override string Description =>
        "コンテンツ単位でのトリガー定義のインポート／エクスポート。" +
        "コンフリクト時の上書き・マージ・スキップ選択、フォーマットバージョン管理を行います（SPEC.md §12 参照）。";
}
