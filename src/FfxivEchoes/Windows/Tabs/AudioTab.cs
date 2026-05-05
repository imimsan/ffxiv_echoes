using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using FfxivEchoes.Actions;

namespace FfxivEchoes.Windows.Tabs;

public sealed class AudioTab : ITab
{
    public string Title => "音声";
    public string Id => "audio";

    private readonly Configuration _configuration;
    private readonly AudioDeviceEnumerator _deviceEnumerator;
    private IReadOnlyList<AudioDeviceInfo> _cachedDevices;
    private DateTime _devicesCachedAt;

    public AudioTab(Configuration configuration, AudioDeviceEnumerator deviceEnumerator)
    {
        _configuration = configuration;
        _deviceEnumerator = deviceEnumerator;
        _cachedDevices = Array.Empty<AudioDeviceInfo>();
        _devicesCachedAt = DateTime.MinValue;
    }

    public void Draw()
    {
        ImGui.TextDisabled("音量は M9 で実装済み（master × カテゴリ × アクション の三階層）。" +
            "TTS のデバイス指定は OS 側のオーディオルーティングに依存します。");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.CollapsingHeader("音量"u8, ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawVolume("マスター音量", () => _configuration.MasterVolume,
                v => _configuration.MasterVolume = v);
            DrawVolume("TTS 音量", () => _configuration.TtsVolume,
                v => _configuration.TtsVolume = v);
            DrawVolume("WAV 音量", () => _configuration.WavVolume,
                v => _configuration.WavVolume = v);
            DrawVolume("フィードバック音量", () => _configuration.FeedbackVolume,
                v => _configuration.FeedbackVolume = v);
        }

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("音声ボイス"u8, ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawDefaultVoice();
        }

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("出力デバイス（WAV）"u8, ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawDeviceDropdown();
        }
    }

    private void DrawVolume(string label, Func<float> getter, Action<float> setter)
    {
        var value = getter();
        ImGui.AlignTextToFramePadding();
        ImGui.Text(label);
        ImGui.SameLine(180f * ImGuiHelpers.GlobalScale);
        ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderFloat($"##vol-{label}", ref value, 0f, 1f, "%.2f"u8))
        {
            setter(value);
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            _configuration.Save();
        }
    }

    private void DrawDefaultVoice()
    {
        var voice = _configuration.DefaultVoice;
        ImGui.AlignTextToFramePadding();
        ImGui.Text("デフォルトボイス");
        ImGui.SameLine(180f * ImGuiHelpers.GlobalScale);
        ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("##default-voice"u8, ref voice, 64))
        {
            _configuration.DefaultVoice = voice;
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            _configuration.Save();
        }
        ImGui.TextDisabled("  例：「Microsoft Haruka Desktop」など。OS の SAPI ボイス名と一致させてください。");
    }

    private void DrawDeviceDropdown()
    {
        // デバイス列挙は重いので 5 秒キャッシュ
        if ((DateTime.UtcNow - _devicesCachedAt).TotalSeconds > 5)
        {
            _cachedDevices = _deviceEnumerator.EnumerateRenderDevices();
            _devicesCachedAt = DateTime.UtcNow;
        }

        var current = _configuration.AudioDevice ?? AudioDeviceEnumerator.SystemDefaultId;
        var currentIndex = 0;
        for (int i = 0; i < _cachedDevices.Count; i++)
        {
            if (_cachedDevices[i].Id == current)
            {
                currentIndex = i;
                break;
            }
        }

        var names = new string[_cachedDevices.Count];
        for (int i = 0; i < _cachedDevices.Count; i++)
        {
            names[i] = _cachedDevices[i].Name;
        }

        ImGui.AlignTextToFramePadding();
        ImGui.Text("出力先");
        ImGui.SameLine(180f * ImGuiHelpers.GlobalScale);
        ImGui.SetNextItemWidth(320f * ImGuiHelpers.GlobalScale);
        if (ImGui.Combo("##audio-device", ref currentIndex, names, names.Length))
        {
            _configuration.AudioDevice = _cachedDevices[currentIndex].Id == AudioDeviceEnumerator.SystemDefaultId
                ? null
                : _cachedDevices[currentIndex].Id;
            _configuration.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("再列挙"u8))
        {
            _devicesCachedAt = DateTime.MinValue;
        }

        ImGui.TextDisabled("  WAV 再生のみ反映。TTS は Windows のオーディオ設定に従います。");
    }
}
