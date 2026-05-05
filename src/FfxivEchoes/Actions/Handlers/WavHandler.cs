using System;
using System.IO;
using System.Media;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Actions.Handlers;

/// <summary>
/// WAV ファイル再生（System.Media.SoundPlayer）。
/// 相対パスは <c>{ConfigDirectory}/sounds/</c> をルートとして解決する。
/// </summary>
/// <remarks>
/// SoundPlayer は音量制御を持たないため、本実装では音量設定は無視される。
/// 音量制御は M9 で別バックエンド（NAudio 等）導入時に対応。
/// </remarks>
public sealed class WavHandler : IActionHandler
{
    public string Type => "wav";

    private readonly Configuration _configuration;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IPluginLog _log;

    public WavHandler(Configuration configuration, IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        _configuration = configuration;
        _pluginInterface = pluginInterface;
        _log = log;
    }

    public void Execute(ActionDefinition action, TriggerFiredEvent context)
    {
        if (string.IsNullOrEmpty(action.File))
        {
            return;
        }
        if (_configuration.MasterVolume <= 0 || _configuration.WavVolume <= 0 || action.Volume <= 0)
        {
            return;
        }

        var resolved = ResolvePath(action.File);
        if (resolved is null)
        {
            _log.Warning("[FfxivEchoes] WAV ファイルが見つかりません：{File}", action.File);
            return;
        }

        try
        {
            using var player = new SoundPlayer(resolved);
            player.Play();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[FfxivEchoes] WAV 再生エラー：{File}", resolved);
        }
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
}
