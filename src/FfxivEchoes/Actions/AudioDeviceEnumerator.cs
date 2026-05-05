using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using NAudio.CoreAudioApi;

namespace FfxivEchoes.Actions;

/// <summary>
/// 利用可能な出力オーディオデバイスを列挙する。
/// </summary>
public sealed class AudioDeviceEnumerator
{
    public const string SystemDefaultId = "(system-default)";
    public const string SystemDefaultName = "システム既定";

    private readonly IPluginLog _log;

    public AudioDeviceEnumerator(IPluginLog log)
    {
        _log = log;
    }

    public IReadOnlyList<AudioDeviceInfo> EnumerateRenderDevices()
    {
        var list = new List<AudioDeviceInfo>
        {
            new(SystemDefaultId, SystemDefaultName),
        };

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    list.Add(new AudioDeviceInfo(device.ID, device.FriendlyName));
                }
                finally
                {
                    device.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[FfxivEchoes] オーディオデバイス列挙に失敗");
        }

        return list;
    }

    public MMDevice? FindDeviceById(string? id)
    {
        if (string.IsNullOrEmpty(id) || id == SystemDefaultId)
        {
            return null;
        }
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                if (string.Equals(device.ID, id, StringComparison.Ordinal))
                {
                    return device;
                }
                device.Dispose();
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[FfxivEchoes] デバイス検索に失敗：{Id}", id);
        }
        return null;
    }
}

public sealed record AudioDeviceInfo(string Id, string Name);
