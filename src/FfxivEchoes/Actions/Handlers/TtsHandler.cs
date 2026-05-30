using System;
using System.IO;
using System.Speech.Synthesis;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;
using FfxivEchoes.Triggers.Models;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace FfxivEchoes.Actions.Handlers;

/// <summary>
/// SAPI5（System.Speech.Synthesis）でテキスト読み上げを行う。
/// SAPI の音量上限（100%）を超えてさらに増幅したい場合は NAudio 経由で再生する。
/// </summary>
public sealed class TtsHandler : IActionHandler, IDisposable
{
    public string Type => "tts";

    private readonly Configuration _configuration;
    private readonly IPluginLog _log;
    private readonly SpeechSynthesizer _synthesizer;
    private readonly object _gate = new();
    // 現在再生中の NAudio プレイヤー。新しい発話が来たら前のを停止して二重鳴りを防ぐ。
    private WaveOutEvent? _currentNAudioPlayer;

    public TtsHandler(Configuration configuration, IPluginLog log)
    {
        _configuration = configuration;
        _log = log;
        _synthesizer = new SpeechSynthesizer();
    }

    public void Execute(ActionDefinition action, TriggerFiredEvent context)
    {
        if (string.IsNullOrEmpty(action.Text))
        {
            return;
        }

        var volume = Math.Clamp(
            _configuration.MasterVolume * _configuration.TtsVolume * action.Volume,
            0.0, 1.0);
        if (volume <= 0)
        {
            return;
        }

        // ブースト倍率（1.0〜5.0）。SAPI の上限を超えて NAudio で増幅する。
        // 3.0 超では元音量によってクリッピングする可能性があるが、ユーザー判断に任せる。
        var boost = Math.Clamp(_configuration.TtsBoost, 1.0f, 5.0f);
        // boost > 1 のときは NAudio 経由（増幅可）。1 のときは SAPI 直出し（軽い）。
        var useNAudio = boost > 1.001f;

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

            _synthesizer.Rate = ClampRate(action.Rate);

            if (useNAudio)
            {
                SpeakViaNAudio(action.Text!, (float)(volume * boost));
            }
            else
            {
                SpeakViaSapiDirect(action.Text!, volume);
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[FfxivEchoes] TTS 発話に失敗（text={Text}）", action.Text);
        }
    }

    private void SpeakViaSapiDirect(string text, double volume)
    {
        lock (_gate)
        {
            try { _synthesizer.SetOutputToDefaultAudioDevice(); } catch { /* ignore */ }
            _synthesizer.Volume = (int)Math.Round(volume * 100);
            _synthesizer.SpeakAsyncCancelAll();
            _synthesizer.SpeakAsync(text);
        }
    }

    /// <summary>
    /// SAPI 出力を MemoryStream に書き出して NAudio で再生。
    /// VolumeSampleProvider で 1.0 を超える増幅が可能（最大 3.0 ≒ +9.5dB）。
    /// </summary>
    private void SpeakViaNAudio(string text, float effectiveVolume)
    {
        // 既存の発話を打ち切り
        try { _synthesizer.SpeakAsyncCancelAll(); } catch { /* ignore */ }

        var ms = new MemoryStream();
        try
        {
            lock (_gate)
            {
                _synthesizer.Volume = 100; // SAPI 側はフル、増幅は NAudio で行う
                _synthesizer.SetOutputToWaveStream(ms);
                _synthesizer.Speak(text); // 同期合成（短い text 想定）
            }
        }
        catch (Exception ex)
        {
            ms.Dispose();
            _log.Error(ex, "[FfxivEchoes] TTS 合成失敗");
            return;
        }

        if (ms.Length == 0)
        {
            ms.Dispose();
            return;
        }
        ms.Position = 0;

        try
        {
            var reader = new WaveFileReader(ms);
            ISampleProvider sample = reader.ToSampleProvider();
            // 増幅。VolumeSampleProvider は >1 でも線形ゲイン
            var amp = new VolumeSampleProvider(sample) { Volume = effectiveVolume };
            var player = new WaveOutEvent();
            player.PlaybackStopped += (_, _) =>
            {
                try { player.Dispose(); reader.Dispose(); ms.Dispose(); } catch { /* ignore */ }
                // 自分が現在のプレイヤーなら参照をクリア（二重 Stop/Dispose 防止）。
                lock (_gate)
                {
                    if (ReferenceEquals(_currentNAudioPlayer, player))
                    {
                        _currentNAudioPlayer = null;
                    }
                }
            };

            // 直前の発話を停止してから新規再生。これをしないと WaveOutEvent が並立して
            // 複数の読み上げが同時に鳴り続ける（重複でうるさい問題の主因）。
            // Stop() は PlaybackStopped を発火させ、上のハンドラが前プレイヤーの資源を解放する。
            WaveOutEvent? previous;
            lock (_gate)
            {
                previous = _currentNAudioPlayer;
                _currentNAudioPlayer = player;
            }
            if (previous is not null)
            {
                try { previous.Stop(); } catch { /* ignore */ }
            }

            player.Init(amp);
            player.Play();
        }
        catch (Exception ex)
        {
            ms.Dispose();
            _log.Error(ex, "[FfxivEchoes] TTS NAudio 再生失敗");
        }
    }

    private void TrySelectVoice(string name)
    {
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

    private static int ClampRate(double rate)
    {
        var mapped = (int)Math.Round((rate - 1.0) * 10.0);
        return Math.Clamp(mapped, -10, 10);
    }

    public void Dispose()
    {
        try
        {
            WaveOutEvent? player;
            lock (_gate)
            {
                player = _currentNAudioPlayer;
                _currentNAudioPlayer = null;
            }
            try { player?.Stop(); } catch { /* ignore */ }
            try { player?.Dispose(); } catch { /* ignore */ }

            _synthesizer.SpeakAsyncCancelAll();
            _synthesizer.Dispose();
        }
        catch
        {
            // dispose 中の例外は飲み込む
        }
    }
}
