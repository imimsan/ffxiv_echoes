using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.SafeZone;

/// <summary>
/// 登録されたプリセット群をディスパッチして計算結果を返す。
/// </summary>
public sealed class SafeZoneEngine
{
    private readonly Dictionary<string, ISafeZonePreset> _presets;
    private readonly IPluginLog _log;

    public SafeZoneEngine(IEnumerable<ISafeZonePreset> presets, IPluginLog log)
    {
        _log = log;
        _presets = new Dictionary<string, ISafeZonePreset>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in presets)
        {
            _presets[p.Method] = p;
        }
    }

    public SafeZoneResult? Calculate(SafeZoneCalculation calc, SafeZoneContext context)
    {
        if (!_presets.TryGetValue(calc.Method, out var preset))
        {
            _log.Warning("[FfxivEchoes] 未知の SafeZone method '{Method}'", calc.Method);
            return null;
        }
        try
        {
            return preset.Calculate(calc, context);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[FfxivEchoes] SafeZone preset '{Method}' で例外", calc.Method);
            return null;
        }
    }
}
