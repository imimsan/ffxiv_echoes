using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;
using FfxivEchoes.SafeZone;
using FfxivEchoes.Triggers.Models;
using FfxivEchoes.Windows;

namespace FfxivEchoes.Actions.Handlers;

public sealed class ArenaViewHandler : IActionHandler
{
    public string Type => "arena_view";

    private readonly MinimapWindow _minimap;
    private readonly SafeZoneEngine _safeZoneEngine;
    private readonly SafeZoneContextBuilder _ctxBuilder;
    private readonly IObjectTable _objectTable;
    private readonly IPluginLog _log;

    public ArenaViewHandler(
        MinimapWindow minimap,
        SafeZoneEngine safeZoneEngine,
        SafeZoneContextBuilder ctxBuilder,
        IObjectTable objectTable,
        IPluginLog log)
    {
        _minimap = minimap;
        _safeZoneEngine = safeZoneEngine;
        _ctxBuilder = ctxBuilder;
        _objectTable = objectTable;
        _log = log;
    }

    public void Execute(ActionDefinition action, TriggerFiredEvent context)
    {
        if (string.IsNullOrEmpty(action.Gimmick))
        {
            return;
        }

        var sourceActor = ResolveSourceActor(context.SourceEvent);
        Vector3? safeWorld = null;
        if (action.SafeZone is not null)
        {
            try
            {
                var ctx = _ctxBuilder.Build(sourceActor, context.SourceEvent);
                var result = _safeZoneEngine.Calculate(action.SafeZone, ctx);
                if (result is not null)
                {
                    safeWorld = result.WorldPosition;
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[FfxivEchoes] arena_view: safe_zone calculation failed");
            }
        }

        var safeRadius = action.Radius is { } rad && rad > 0 ? (float)rad : 3.0f;
        var sourceWorld = sourceActor is null
            ? (Vector3?)null
            : new Vector3(sourceActor.Position.X, sourceActor.Position.Y, sourceActor.Position.Z);
        var useDynamicFacing =
            ArenaProjection.UsesFacing(action.Gimmick) &&
            (string.IsNullOrWhiteSpace(action.Direction) ||
             string.Equals(action.Direction, "N", StringComparison.OrdinalIgnoreCase));
        var directionAngleRad = useDynamicFacing && sourceActor is not null
            ? ArenaProjection.RotationToMapAngleRad(sourceActor.Rotation)
            : (float?)null;

        _minimap.AddArenaView(
            gimmick: action.Gimmick,
            callout: action.Callout,
            durationSec: action.Duration ?? 5.0,
            direction: action.Direction,
            fanDeg: action.FanDeg,
            arenaRadius: action.ArenaRadius,
            safeZoneWorld: safeWorld,
            safeZoneRadius: safeRadius,
            directionAngleRad: directionAngleRad,
            sourceWorld: sourceWorld,
            strategyPositions: action.StrategyPositions);
    }

    private IBattleChara? ResolveSourceActor(IGameEvent sourceEvent)
    {
        var sourceId = sourceEvent switch
        {
            CastStartedEvent cast => cast.SourceId,
            CastCompletedEvent cast => cast.SourceId,
            CastCanceledEvent cast => cast.SourceId,
            ActionUsedEvent action => action.SourceId,
            _ => 0u,
        };

        return sourceId == 0 ? null : _objectTable.SearchById(sourceId) as IBattleChara;
    }
}
