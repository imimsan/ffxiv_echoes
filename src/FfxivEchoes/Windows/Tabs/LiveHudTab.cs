namespace FfxivEchoes.Windows.Tabs;

public sealed class LiveHudTab : PlaceholderTab
{
    public override string Title => "ライブHUD";
    public override string Id => "live-hud";
    protected override string PlannedFor => "F3";
    protected override string Description =>
        "戦闘中オーバーレイ（ハイブリッドタイムライン）の位置・サイズ・表示モード設定。" +
        "sync 機構による時刻補正のオプションもここで管理する予定です（SPEC.md §8 参照）。";
}
