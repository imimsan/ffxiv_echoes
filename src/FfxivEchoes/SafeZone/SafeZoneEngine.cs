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

    // IntersectionPreset 等が constraints 経由で Calculate を再帰呼び出しできる。
    // constraints に自分自身(method=intersection)を書くと無限再帰になり、StackOverflowException は
    // CLR レベルで catch 不能（プロセス強制終了）。深度カウンタで事前に打ち切る。
    [ThreadStatic] private static int _depth;
    private const int MaxDepth = 8;

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

        if (_depth >= MaxDepth)
        {
            _log.Warning("[FfxivEchoes] SafeZone 再帰深度 {Max} 超過（method '{Method}'）。循環参照の可能性。",
                MaxDepth, calc.Method);
            return null;
        }

        _depth++;
        try
        {
            return preset.Calculate(calc, context);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[FfxivEchoes] SafeZone preset '{Method}' で例外", calc.Method);
            return null;
        }
        finally
        {
            _depth--;
        }
    }
}
