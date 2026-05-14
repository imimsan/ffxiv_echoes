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

            // TTS ブースト：1.0 = SAPI 標準、最大 5.0（≒ +14dB）
            ImGui.Spacing();
            var boost = _configuration.TtsBoost;
            ImGui.AlignTextToFramePadding();
            ImGui.Text("TTS ブースト");
            ImGui.SameLine(180f * ImGuiHelpers.GlobalScale);
            ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
            if (ImGui.SliderFloat("##tts-boost", ref boost, 1.0f, 5.0f, "%.2fx"))
            {
                _configuration.TtsBoost = Math.Clamp(boost, 1.0f, 5.0f);
            }
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                _configuration.Save();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "SAPI（Windows 音声合成）の音量上限を超えて TTS をさらに増幅する。\n" +
                    "1.0x = 既定（SAPI そのまま）\n" +
                    "2.0x ≒ +6dB\n" +
                    "3.0x ≒ +9.5dB\n" +
                    "5.0x ≒ +14dB（最大）\n" +
                    "「TTS が小さい」ときはここを上げる。\n" +
                    "ただし 3.0x を超えると元音源によってクリッピング（音割れ）の可能性あり。");
            }
            // 3.0 超え時は警告表示
            if (_configuration.TtsBoost > 3.0f)
            {
                ImGui.SameLine();
                ImGui.TextColored(new System.Numerics.Vector4(1.0f, 0.7f, 0.2f, 1.0f),
                    "⚠ 音割れ可能性");
            }
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
