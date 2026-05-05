using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace FfxivEchoes.Windows.Tabs;

public sealed class AudioTab : ITab
{
    public string Title => "音声";
    public string Id => "audio";

    private readonly Configuration _configuration;

    public AudioTab(Configuration configuration)
    {
        _configuration = configuration;
    }

    public void Draw()
    {
        ImGui.TextDisabled("M9 で本実装：実際の音はまだ鳴りません。スライダーは設定の保存のみ動作します。");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.CollapsingHeader("音量"u8, ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawMasterVolume();
            DrawTtsVolume();
            DrawWavVolume();
            DrawFeedbackVolume();
        }

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("音声ボイス"u8, ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawDefaultVoice();
            ImGui.Spacing();
            DrawAudioDevice();
        }
    }

    private void DrawMasterVolume()
    {
        var v = _configuration.MasterVolume;
        LabelColumn("マスター音量");
        ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderFloat("##master-volume"u8, ref v, 0f, 1f, "%.2f"u8))
        {
            _configuration.MasterVolume = v;
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            _configuration.Save();
        }
    }

    private void DrawTtsVolume()
    {
        var v = _configuration.TtsVolume;
        LabelColumn("TTS 音量");
        ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderFloat("##tts-volume"u8, ref v, 0f, 1f, "%.2f"u8))
        {
            _configuration.TtsVolume = v;
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            _configuration.Save();
        }
    }

    private void DrawWavVolume()
    {
        var v = _configuration.WavVolume;
        LabelColumn("WAV 音量");
        ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderFloat("##wav-volume"u8, ref v, 0f, 1f, "%.2f"u8))
        {
            _configuration.WavVolume = v;
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            _configuration.Save();
        }
    }

    private void DrawFeedbackVolume()
    {
        var v = _configuration.FeedbackVolume;
        LabelColumn("フィードバック音量");
        ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderFloat("##feedback-volume"u8, ref v, 0f, 1f, "%.2f"u8))
        {
            _configuration.FeedbackVolume = v;
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            _configuration.Save();
        }
    }

    private void DrawDefaultVoice()
    {
        var voice = _configuration.DefaultVoice;
        LabelColumn("デフォルトボイス");
        ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("##default-voice"u8, ref voice, 64))
        {
            _configuration.DefaultVoice = voice;
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            _configuration.Save();
        }
        ImGui.TextDisabled("  例：ja-JP-Default。利用可能なボイス一覧の選択 UI は M9 で追加");
    }

    private void DrawAudioDevice()
    {
        var device = _configuration.AudioDevice ?? string.Empty;
        LabelColumn("出力デバイス");
        ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("##audio-device"u8, ref device, 128))
        {
            _configuration.AudioDevice = string.IsNullOrWhiteSpace(device) ? null : device;
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            _configuration.Save();
        }
        ImGui.TextDisabled("  空欄でシステム既定。デバイス一覧 UI は M9 で追加");
    }

    private static void LabelColumn(string label)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text(label);
        ImGui.SameLine(180f * ImGuiHelpers.GlobalScale);
    }
}
