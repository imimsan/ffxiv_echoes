using System;
using System.Collections.Generic;
using System.IO;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;
using FfxivEchoes.Triggers.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace FfxivEchoes.Actions.Handlers;

/// <summary>
/// WAV ファイル再生（NAudio）。三階層音量（master * wav * action）を適用し、
/// Configuration.AudioDevice の指定があれば該当デバイスへ出力。
/// </summary>
public sealed class WavHandler : IActionHandler, IDisposable
{
    public string Type => "wav";

    private readonly Configuration _configuration;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly AudioDeviceEnumerator _deviceEnumerator;
    private readonly IPluginLog _log;
    // 再生中のプレイヤーを追跡し、Dispose（プラグイン解放 / xlrestart）で確実に停止・破棄する。
    // 追跡しないと、解放後に PlaybackStopped が解放間際の資源を触ってクラッシュしうる。
    private readonly object _gate = new();
    private readonly HashSet<IWavePlayer> _active = new();

    public WavHandler(
        Configuration configuration,
        IDalamudPluginInterface pluginInterface,
        AudioDeviceEnumerator deviceEnumerator,
        IPluginLog log)
    {
        _configuration = configuration;
        _pluginInterface = pluginInterface;
        _deviceEnumerator = deviceEnumerator;
        _log = log;
    }

    public void Execute(ActionDefinition action, TriggerFiredEvent context)
    {
        if (string.IsNullOrEmpty(action.File))
        {
            return;
        }

        var volume = (float)Math.Clamp(
            _configuration.MasterVolume * _configuration.WavVolume * action.Volume,
            0.0, 1.0);
        if (volume <= 0)
        {
            return;
        }

        var resolved = ResolvePath(action.File);
        if (resolved is null)
        {
            _log.Warning("[FfxivEchoes] WAV ファイルが見つかりません：{File}", action.File);
            return;
        }

        // 各再生は独立したライフサイクル：ファイル読込→出力デバイス→再生→停止後 Dispose。
        // PlaybackStopped で Dispose チェーンを発火させる fire-and-forget スタイル。
        try
        {
            var reader = new AudioFileReader(resolved) { Volume = volume };
            IWavePlayer player = CreatePlayer();

            player.PlaybackStopped += (_, _) =>
            {
                try
                {
                    player.Dispose();
                    reader.Dispose();
                }
                catch
                {
                    // Dispose 中の例外は無視
                }
                lock (_gate)
                {
                    _active.Remove(player);
                }
            };

            lock (_gate)
            {
                _active.Add(player);
            }
            player.Init(reader);
            player.Play();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[FfxivEchoes] WAV 再生エラー：{File}", resolved);
        }
    }

    private IWavePlayer CreatePlayer()
    {
        var deviceId = _configuration.AudioDevice;
        if (!string.IsNullOrEmpty(deviceId) && deviceId != AudioDeviceEnumerator.SystemDefaultId)
        {
            var device = _deviceEnumerator.FindDeviceById(deviceId);
            if (device is not null)
            {
                return new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
            }
            _log.Warning("[FfxivEchoes] 指定デバイス '{Id}' が見つかりません。既定デバイスを使用", deviceId);
        }
        return new WaveOutEvent();
    }

    private string? ResolvePath(string file)
    {
        if (Path.IsPathRooted(file) && File.Exists(file))
        {
            return file;
        }

        var soundsDir = Path.Combine(_pluginInterface.ConfigDirectory.FullName, "sounds");
        var candidate = Path.Combine(soundsDir, file);
        return File.Exists(candidate) ? candidate : null;
    }

    public void Dispose()
    {
        // 再生中のプレイヤーをすべて停止・破棄する。Stop() は PlaybackStopped を発火させ
        // 上のハンドラが資源を解放する。Stop() は _gate の外で呼び（PlaybackStopped が
        // _gate を取るためデッドロック回避）、スナップショットだけ _gate 内で取る。
        List<IWavePlayer> players;
        lock (_gate)
        {
            players = new List<IWavePlayer>(_active);
            _active.Clear();
        }
        foreach (var player in players)
        {
            try { player.Stop(); } catch { /* ignore */ }
            try { player.Dispose(); } catch { /* ignore */ }
        }
    }
}
