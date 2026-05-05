using System.Numerics;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;
using FfxivEchoes.SafeZone;
using FfxivEchoes.Triggers.Models;
using FfxivEchoes.Windows;

namespace FfxivEchoes.Actions.Handlers;

/// <summary>
/// 俯瞰アリーナ図を表示するアクション（arena_view）。
/// </summary>
/// <remarks>
/// gimmick タイプ（outer_ring / inner_circle / scatter / stack / cone）と callout 文字列を受け取り、
/// MinimapWindow に転送する。任意で safe_zone（F4-F7 の SafeZoneCalculation）を受け取ると、
/// その時点で計算した世界座標をミニマップ上に緑マーカーとして重畳描画する。
/// </remarks>
public sealed class ArenaViewHandler : IActionHandler
{
    public string Type => "arena_view";

    private readonly MinimapWindow _minimap;
    private readonly SafeZoneEngine _safeZoneEngine;
    private readonly SafeZoneContextBuilder _ctxBuilder;
    private readonly IPluginLog _log;

    public ArenaViewHandler(
        MinimapWindow minimap,
        SafeZoneEngine safeZoneEngine,
        SafeZoneContextBuilder ctxBuilder,
        IPluginLog log)
    {
        _minimap = minimap;
        _safeZoneEngine = safeZoneEngine;
        _ctxBuilder = ctxBuilder;
        _log = log;
    }

    public void Execute(ActionDefinition action, TriggerFiredEvent context)
    {
        if (string.IsNullOrEmpty(action.Gimmick))
        {
            return;
        }

        // safe_zone が指定されていれば計算
        Vector3? safeWorld = null;
        if (action.SafeZone is not null)
        {
            try
            {
                var ctx = _ctxBuilder.Build(lastEvent: context.SourceEvent);
                var result = _safeZoneEngine.Calculate(action.SafeZone, ctx);
                if (result is not null)
                {
                    safeWorld = result.WorldPosition;
                }
            }
            catch (System.Exception ex)
            {
                _log.Warning(ex, "[FfxivEchoes] arena_view: safe_zone 計算失敗（描画はスキップ）");
            }
        }

        var safeRadius = action.Radius is { } rad && rad > 0 ? (float)rad : 3.0f;

        _minimap.AddArenaView(
            gimmick: action.Gimmick,
            callout: action.Callout,
            durationSec: action.Duration ?? 5.0,
            direction: action.Direction,
            fanDeg: action.FanDeg,
            arenaRadius: action.ArenaRadius,
            safeZoneWorld: safeWorld,
            safeZoneRadius: safeRadius);
    }
}
