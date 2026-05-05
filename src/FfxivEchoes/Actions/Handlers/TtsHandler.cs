using System;
using System.Speech.Synthesis;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Actions.Handlers;

/// <summary>
/// SAPI5（System.Speech.Synthesis）でテキスト読み上げを行う。
/// Windows 専用。日本語ボイスは OS の音声合成エンジンに依存。
/// </summary>
public sealed class TtsHandler : IActionHandler, IDisposable
{
    public string Type => "tts";

    private readonly Configuration _configuration;
    private readonly IPluginLog _log;
    private readonly SpeechSynthesizer _synthesizer;
    private readonly object _gate = new();

    public TtsHandler(Configuration configuration, IPluginLog log)
    {
        _configuration = configuration;
        _log = log;
        _synthesizer = new SpeechSynthesizer();
        try
        {
            _synthesizer.SetOutputToDefaultAudioDevice();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[FfxivEchoes] TTS の出力デバイス初期化に失敗");
        }
    }

    public void Execute(ActionDefinition action, TriggerFiredEvent context)
    {
        if (string.IsNullOrEmpty(action.Text))
        {
            return;
        }

        // 音量計算：master * tts * action（SPEC.md §10.2 三階層）
        var volume = Math.Clamp(
            _configuration.MasterVolume * _configuration.TtsVolume * action.Volume,
            0.0, 1.0);
        if (volume <= 0)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                if (!string.IsNullOrEmpty(action.Voice))
                {
                    TrySelectVoice(action.Voice);
                }
                else if (!string.IsNullOrEmpty(_configuration.DefaultVoice))
                {
                    TrySelectVoice(_configuration.DefaultVoice);
                }

                _synthesizer.Volume = (int)Math.Round(volume * 100);
                _synthesizer.Rate = ClampRate(action.Rate);
                _synthesizer.SpeakAsyncCancelAll();
                _synthesizer.SpeakAsync(action.Text);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[FfxivEchoes] TTS 発話に失敗（text={Text}）", action.Text);
            }
        }
    }

    private void TrySelectVoice(string name)
    {
        // SAPI のボイス名は環境依存（ja-JP-Default のような汎用名は SAPI にはない）。
        // 名前一致を試み、失敗時はデフォルトを維持。
        try
        {
            foreach (var voice in _synthesizer.GetInstalledVoices())
            {
                var info = voice.VoiceInfo;
                if (string.Equals(info.Name, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(info.Culture.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    _synthesizer.SelectVoice(info.Name);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[FfxivEchoes] TTS ボイス '{Voice}' の選択に失敗（既定を使用）", name);
        }
    }

    /// <summary>SAPI の Rate は -10〜+10。1.0 倍を 0 にマップする。</summary>
    private static int ClampRate(double rate)
    {
        // 0.5 倍 → -5、1.0 倍 → 0、2.0 倍 → 10 になるような簡易マッピング
        var mapped = (int)Math.Round((rate - 1.0) * 10.0);
        return Math.Clamp(mapped, -10, 10);
    }

    public void Dispose()
    {
        try
        {
            _synthesizer.SpeakAsyncCancelAll();
            _synthesizer.Dispose();
        }
        catch
        {
            // dispose 中の例外は飲み込む
        }
    }
}
