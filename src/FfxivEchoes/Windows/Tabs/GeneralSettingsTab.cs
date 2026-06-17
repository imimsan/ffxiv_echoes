using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Plugin;

namespace FfxivEchoes.Windows.Tabs;

public sealed class GeneralSettingsTab : ITab
{
    public string Title => "全体設定";
    public string Id => "general";

    private readonly Configuration _configuration;
    private readonly IDalamudPluginInterface _pluginInterface;

    public GeneralSettingsTab(Configuration configuration, IDalamudPluginInterface pluginInterface)
    {
        _configuration = configuration;
        _pluginInterface = pluginInterface;
    }

    public void Draw()
    {
        ImGui.Text("プラグイン情報");
        ImGui.Separator();
        var manifest = _pluginInterface.Manifest;
        ImGui.TextUnformatted($"バージョン: {manifest.AssemblyVersion}");
        ImGui.TextUnformatted($"Dalamud API レベル: {manifest.DalamudApiLevel}");
        ImGui.TextUnformatted($"設定ファイル: {_pluginInterface.ConfigFile.FullName}");
        ImGui.Spacing();

        ImGui.Text("動作");
        ImGui.Separator();

        var debug = _configuration.DebugMode;
        if (ImGui.Checkbox("デバッグモード", ref debug))
        {
            _configuration.DebugMode = debug;
            _configuration.Save();
        }
        ImGui.TextDisabled("  詳細ログをチャットに出力します。本番運用時は OFF を推奨。");
        ImGui.Spacing();

        var echo = _configuration.EchoTriggerFires;
        if (ImGui.Checkbox("トリガー発火をチャットに表示", ref echo))
        {
            _configuration.EchoTriggerFires = echo;
            _configuration.Save();
        }
        ImGui.TextDisabled("  音声 / オーバーレイが鳴らない時の動作確認用フォールバック。");
        ImGui.Spacing();

        var autoVis = _configuration.AutoVisualForTts;
        if (ImGui.Checkbox("TTS のみのトリガーで自動オーバーレイ", ref autoVis))
        {
            _configuration.AutoVisualForTts = autoVis;
            _configuration.Save();
        }
        ImGui.TextDisabled("  TTS だけのトリガー発火時にも、同じ文字を画面中央に 3 秒表示する。");
        ImGui.Spacing();

        // 言語表示（SPEC §1.5：日本語クライアント前提のため当面ロックされた値）
        ImGui.AlignTextToFramePadding();
        ImGui.Text("言語");
        ImGui.SameLine(180f * ImGuiHelpers.GlobalScale);
        ImGui.TextDisabled($"{_configuration.Language}（日本語クライアント前提・固定）");
        ImGui.Spacing();

        // マージ許容範囲
        var tolerance = (float)_configuration.MergeToleranceSeconds;
        ImGui.AlignTextToFramePadding();
        ImGui.Text("マージ許容範囲（秒）");
        ImGui.SameLine(180f * ImGuiHelpers.GlobalScale);
        ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderFloat("##merge-tolerance", ref tolerance, 0f, 10f, "%.1f s"))
        {
            _configuration.MergeToleranceSeconds = tolerance;
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            _configuration.Save();
        }
        ImGui.TextDisabled("  複数戦闘ログをマージする際の同一イベント判定の時間許容範囲。");
        ImGui.Spacing();

        // 録画保持日数
        var retention = _configuration.LogRetentionDays ?? 0;
        ImGui.AlignTextToFramePadding();
        ImGui.Text("録画ログ保持日数");
        ImGui.SameLine(180f * ImGuiHelpers.GlobalScale);
        ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("##log-retention", ref retention))
        {
            if (retention <= 0)
            {
                _configuration.LogRetentionDays = null;
            }
            else
            {
                _configuration.LogRetentionDays = retention;
            }
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            _configuration.Save();
        }
        ImGui.TextDisabled("  0 以下で「無期限保持」（デフォルト）。N 日経過したログを自動削除します。");
        ImGui.Spacing();

        ImGui.Text("俯瞰図（ミニマップ）");
        ImGui.Separator();

        var showPredictedAoe = _configuration.ShowPredictedAoeOnMinimap;
        if (ImGui.Checkbox("予測 AoE を事前表示する", ref showPredictedAoe))
        {
            _configuration.ShowPredictedAoeOnMinimap = showPredictedAoe;
            _configuration.Save();
        }
        ImGui.TextDisabled("  タイムライン予測の技範囲を、詠唱開始前から俯瞰図に薄く表示します。");

        var advance = (float)_configuration.PredictedAoeAdvanceSec;
        ImGui.AlignTextToFramePadding();
        ImGui.Text("事前表示の秒数");
        ImGui.SameLine(180f * ImGuiHelpers.GlobalScale);
        ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderFloat("##predicted-aoe-advance", ref advance, 1f, 30f, "%.0f s"))
        {
            _configuration.PredictedAoeAdvanceSec = Math.Clamp(Math.Round(advance), 1.0, 30.0);
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            _configuration.Save();
        }

        ImGui.Spacing();
        ImGui.Text("HUD");
        ImGui.Separator();

        var showDebuffHud = _configuration.ShowDebuffHud;
        if (ImGui.Checkbox("自分のデバフ一覧を表示", ref showDebuffHud))
        {
            _configuration.ShowDebuffHud = showDebuffHud;
            _configuration.Save();
        }
        ImGui.TextDisabled("  戦闘中、自分に付いた弱体の名前と残り秒を一覧表示します。");
    }
}
