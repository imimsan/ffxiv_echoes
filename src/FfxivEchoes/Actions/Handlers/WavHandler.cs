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

    /// <summary>同一ファイルの連続再生を抑える dedup 窓（秒）。proximity_feedback 等の直呼び経路も覆う。</summary>
    public const double DedupWindowSeconds = 0.5;

    private readonly Configuration _configuration;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly AudioDeviceEnumerator _deviceEnumerator;
    private readonly IPluginLog _log;
    private readonly object _dedupGate = new();
    private readonly Dictionary<string, DateTimeOffset> _recentPlays = new(StringComparer.OrdinalIgnoreCase);
    // 再生中のプレイヤーと付随資源（reader / WASAPI 用 MMDevice）を追跡し、
    // Dispose（プラグイン解放 / xlrestart）で確実に停止・破棄する。
    // 追跡しないと、解放後に PlaybackStopped が解放間際の資源を触ってクラッシュしうる。
    // MMDevice(COM) は WasapiOut.Dispose では解放されないため自前で追跡する（指定デバイス時のリーク対策）。
    private readonly object _gate = new();
    private readonly Dictionary<IWavePlayer, Playback> _active = new();

    private readonly record struct Playback(AudioFileReader Reader, MMDevice? Device);

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

        // 入口 dedup：同一ファイルの極短時間連発（2 体同名技 / proximity_feedback 直呼び）を抑制。
        lock (_dedupGate)
        {
            if (!ShouldPlayByDedup(action.File, DateTimeOffset.UtcNow, _recentPlays, DedupWindowSeconds))
            {
                return;
            }
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
        AudioFileReader? reader = null;
        IWavePlayer? player = null;
        MMDevice? device = null;
        try
        {
            reader = new AudioFileReader(resolved) { Volume = volume };
            (player, device) = CreatePlayer();

            var capturedPlayer = player;
            var capturedReader = reader;
            var capturedDevice = device;
            capturedPlayer.PlaybackStopped += (_, _) =>
            {
                // 二重破棄を避けるため、_active から取り外せた側だけが資源を解放する
                // （Dispose() が先に取り外していればそちらが解放する）。
                bool owned;
                lock (_gate)
                {
                    owned = _active.Remove(capturedPlayer);
                }
                if (!owned)
                {
                    return;
                }
                try { capturedPlayer.Dispose(); } catch { /* Dispose 中の例外は無視 */ }
                try { capturedReader.Dispose(); } catch { /* ignore */ }
                try { capturedDevice?.Dispose(); } catch { /* ignore */ }
            };

            lock (_gate)
            {
                _active[player] = new Playback(reader, device);
            }
            player.Init(reader);
            player.Play();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[FfxivEchoes] WAV 再生エラー：{File}", resolved);
            // 登録前に失敗した場合のみここで後始末する（登録済みなら所有権は Dispose() 側）。
            bool registered;
            lock (_gate)
            {
                registered = player is not null && _active.ContainsKey(player);
            }
            if (!registered)
            {
                try { player?.Dispose(); } catch { /* ignore */ }
                try { reader?.Dispose(); } catch { /* ignore */ }
                try { device?.Dispose(); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>
    /// 同一ファイルの連続再生を抑制する（窓は初回起点）。ファイル未指定は常に通す。
    /// </summary>
    public static bool ShouldPlayByDedup(
        string? file, DateTimeOffset now,
        Dictionary<string, DateTimeOffset> recent, double windowSec)
    {
        if (string.IsNullOrEmpty(file))
        {
            return true;
        }
        if (recent.TryGetValue(file, out var last) && (now - last).TotalSeconds < windowSec)
        {
            return false;
        }
        recent[file] = now;
        return true;
    }

    private (IWavePlayer Player, MMDevice? Device) CreatePlayer()
    {
        var deviceId = _configuration.AudioDevice;
        if (!string.IsNullOrEmpty(deviceId) && deviceId != AudioDeviceEnumerator.SystemDefaultId)
        {
            var device = _deviceEnumerator.FindDeviceById(deviceId);
            if (device is not null)
            {
                try
                {
                    return (new WasapiOut(device, AudioClientShareMode.Shared, true, 100), device);
                }
                catch
                {
                    // WasapiOut 生成失敗時に MMDevice(COM) を取りこぼさない。
                    device.Dispose();
                    throw;
                }
            }
            _log.Warning("[FfxivEchoes] 指定デバイス '{Id}' が見つかりません。既定デバイスを使用", deviceId);
        }
        return (new WaveOutEvent(), null);
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
        // 再生中のプレイヤーをすべて停止・破棄する。先に _active から取り外して所有権を確定させ、
        // その後 Stop() を呼ぶ（Stop() が発火させる PlaybackStopped は _active.Remove に失敗し
        // 二重破棄しない）。Stop() は _gate の外で呼ぶ（ハンドラが _gate を取るためデッドロック回避）。
        List<KeyValuePair<IWavePlayer, Playback>> snapshot;
        lock (_gate)
        {
            snapshot = new List<KeyValuePair<IWavePlayer, Playback>>(_active);
            _active.Clear();
        }
        foreach (var (player, playback) in snapshot)
        {
            try { player.Stop(); } catch { /* ignore */ }
            try { player.Dispose(); } catch { /* ignore */ }
            try { playback.Reader.Dispose(); } catch { /* ignore */ }
            try { playback.Device?.Dispose(); } catch { /* ignore */ }
        }
    }
}
