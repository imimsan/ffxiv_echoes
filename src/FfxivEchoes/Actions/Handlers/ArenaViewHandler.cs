using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;
using FfxivEchoes.SafeZone;
using FfxivEchoes.Triggers;
using FfxivEchoes.Triggers.Models;
using FfxivEchoes.Utils;
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
    private readonly TriggerStore? _triggerStore;

    public ArenaViewHandler(
        MinimapWindow minimap,
        SafeZoneEngine safeZoneEngine,
        SafeZoneContextBuilder ctxBuilder,
        IObjectTable objectTable,
        IPluginLog log,
        TriggerStore? triggerStore = null)
    {
        _minimap = minimap;
        _safeZoneEngine = safeZoneEngine;
        _ctxBuilder = ctxBuilder;
        _objectTable = objectTable;
        _log = log;
        _triggerStore = triggerStore;
    }

    public void Execute(ActionDefinition action, TriggerFiredEvent context)
    {
        var hasUserLayout =
            action.SafeZone is not null ||
            (action.StrategyPositions?.Count ?? 0) > 0 ||
            (action.ObjectMarkers?.Count ?? 0) > 0 ||
            (action.AoeZones?.Count ?? 0) > 0;
        var gimmick = string.IsNullOrWhiteSpace(action.Gimmick)
            ? (hasUserLayout ? "user_layout" : null)
            : action.Gimmick;
        if (string.IsNullOrEmpty(gimmick))
        {
            return;
        }

        // raid-wide マーク済キャストはミニマップに描画しない（自動 AoE 描画用の skip）。
        // ただし「ユーザーが明示的に作った layout（AoE zones / positions / markers / safezone）」
        // がある場合は、ユーザーの意思を尊重して表示する。
        // 「全体攻撃にマークしたから自動 AoE は出さない、でも手動メカニクスは出したい」という
        // 普通のユースケースをカバー。
        var file = _triggerStore?.GetByZone(context.Zone);
        var suppressMinimap = AutoSafeCallPlanner.ShouldSuppressMinimap(file, context.SourceEvent);
        if (!hasUserLayout && suppressMinimap)
        {
            _log.Information(
                "[FfxivEchoes] arena_view: 全体攻撃マーク済のためミニマップ非表示 trigger={Id} (user layout 無し)",
                context.TriggerId);
            return;
        }
        if (hasUserLayout && suppressMinimap)
        {
            _log.Information(
                "[FfxivEchoes] arena_view: 全体攻撃マーク済だが user layout があるので表示 trigger={Id}",
                context.TriggerId);
        }

        var sourceActor = ResolveSourceActor(context.SourceEvent);
        Vector3? safeWorld = null;
        Vector3? lockedCenter = null;
        try
        {
            var ctx = _ctxBuilder.Build(sourceActor, context.SourceEvent);

            // この瞬間のアリーナ中心をロック（以降表示中はジッタしない）。
            // 優先：プロファイル校正済中心（ユーザー手動設定）
            //  → 無ければ snapshot.ArenaCenter（ボス位置単独に変更済）
            if (action.ArenaCenterX.HasValue && action.ArenaCenterZ.HasValue)
            {
                lockedCenter = new Vector3(
                    (float)action.ArenaCenterX.Value,
                    ctx.ArenaCenter.Y,
                    (float)action.ArenaCenterZ.Value);
            }
            else
            {
                lockedCenter = ctx.ArenaCenter;
            }

            // 安置計算もロック済み中心を基点にする。これが無いと ArenaCenterRelative 系プリセットが
            // ボスのライブ位置（ctx.ArenaCenter の既定）を中心に計算し、固定中心設定が無視される。
            ctx = ctx with { ArenaCenter = lockedCenter.Value };

            if (action.SafeZone is not null)
            {
                var result = _safeZoneEngine.Calculate(action.SafeZone, ctx);
                if (result is not null)
                {
                    safeWorld = result.WorldPosition;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[FfxivEchoes] arena_view: ctx build / safe_zone failed");
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

        var resolvedAoeZones = ResolveDynamicAoeZones(action.AoeZones, context.SourceEvent, lockedCenter, sourceWorld);

        // suppress_auto_aoe を持つ user 定義 zone があれば、同 cast id の Lumina 自動経路で
        // 入った ArenaItem をミニマップから除去する（AoE 統一フェーズ 1：床塗り側で先に
        // 実装した SuppressAutoLumina をミニマップにも反映）。
        if (action.AoeZones is { } zones &&
            zones.Any(z => z.SuppressAutoAoe))
        {
            var castId = ExtractCastIdForSuppress(context.SourceEvent);
            if (castId != 0)
            {
                _minimap.SuppressAutoLuminaForCast(castId);
            }
        }

        _minimap.AddArenaView(
            gimmick: gimmick,
            callout: action.Callout,
            durationSec: action.Duration ?? 5.0,
            direction: action.Direction,
            fanDeg: action.FanDeg,
            arenaRadius: action.ArenaRadius,
            safeZoneWorld: safeWorld,
            safeZoneRadius: safeRadius,
            directionAngleRad: directionAngleRad,
            sourceWorld: sourceWorld,
            strategyPositions: action.StrategyPositions,
            objectMarkers: action.ObjectMarkers,
            aoeZones: resolvedAoeZones,
            partyStatusHighlights: action.PartyStatusHighlights,
            arenaShape: action.ArenaShape,
            arenaWidth: action.ArenaWidth,
            arenaDepth: action.ArenaDepth,
            lockedArenaCenter: lockedCenter);
    }

    private static uint ExtractCastIdForSuppress(IGameEvent ev) => ev switch
    {
        CastStartedEvent c => c.CastActionId,
        CastCompletedEvent c => c.CastActionId,
        ActionUsedEvent a => a.ActionId,
        _ => 0u,
    };

    private static IReadOnlyList<StrategyAoeZone>? ResolveDynamicAoeZones(
        IReadOnlyList<StrategyAoeZone>? zones,
        IGameEvent sourceEvent,
        Vector3? lockedCenter,
        Vector3? sourceWorld)
    {
        if (zones is null || zones.Count == 0)
        {
            return zones;
        }

        var center = lockedCenter ?? Vector3.Zero;
        var result = new List<StrategyAoeZone>(zones.Count);
        foreach (var zone in zones)
        {
            var anchor = zone.Anchor?.Trim().ToLowerInvariant();
            switch (anchor)
            {
                case "source_actor" when sourceWorld is { } sw:
                    result.Add(CloneAt(zone, sw - center));
                    break;
                case "matched_object" when sourceEvent is ObjectAppearedEvent obj:
                    result.Add(CloneAt(zone, obj.Position - center, pointDirectionalToCenter: true));
                    break;
                case "matched_object" when sourceEvent is ObjectGroupAppearedEvent group && group.Positions.Count > 0:
                    result.Add(CloneAt(zone, group.Positions[0] - center, pointDirectionalToCenter: true));
                    break;
                case "each_matched_object" when sourceEvent is ObjectGroupAppearedEvent group:
                    foreach (var pos in group.Positions)
                    {
                        result.Add(CloneAt(zone, pos - center, pointDirectionalToCenter: true));
                    }
                    break;
                case "each_matched_object" when sourceEvent is ObjectAppearedEvent obj:
                    result.Add(CloneAt(zone, obj.Position - center, pointDirectionalToCenter: true));
                    break;
                case "waymark":
                    var live = FfxivEchoes.Capture.WaymarkProvider.TryGetPosition(zone.AnchorWaymark);
                    result.Add(live is { } wp ? CloneAt(zone, wp - center) : zone);
                    break;
                default:
                    result.Add(zone);
                    break;
            }
        }

        return result;
    }

    private static StrategyAoeZone CloneAt(
        StrategyAoeZone zone,
        Vector3 relative,
        bool pointDirectionalToCenter = false)
    {
        var x = relative.X + zone.X;
        var z = relative.Z + zone.Z;
        var rotationDeg = zone.RotationDeg;
        if (rotationDeg is null && pointDirectionalToCenter && ShouldPointToArenaCenter(zone.Shape))
        {
            rotationDeg = AoeGeometryPolicy.RotationDegTowardsArenaCenter((float)x, (float)z);
        }

        return new StrategyAoeZone
        {
            Id = zone.Id,
            Label = zone.Label,
            Shape = zone.Shape,
            X = x,
            Z = z,
            RadiusM = zone.RadiusM,
            InnerRadiusM = zone.InnerRadiusM,
            RotationDeg = rotationDeg,
            FanDeg = zone.FanDeg,
            HalfWidthM = zone.HalfWidthM,
            IncludeHitbox = zone.IncludeHitbox,
            RotationOffsetDeg = zone.RotationOffsetDeg,
            Color = zone.Color,
            IsDanger = zone.IsDanger,
            Anchor = "static",
            AnchorWaymark = zone.AnchorWaymark,
            ActorMatcher = zone.ActorMatcher,
            StateFilter = zone.StateFilter,
            RotationSource = zone.RotationSource,
            LiveFloorPaint = zone.LiveFloorPaint,
            DurationSec = zone.DurationSec,
            SuppressAutoAoe = zone.SuppressAutoAoe,
        };
    }

    private static bool ShouldPointToArenaCenter(string? shape)
    {
        return (shape ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "cone" or "rect" or "line" or "cross" or "half_plane" or "halfplane" or "donut_cone" or "donutcone" => true,
            _ => false,
        };
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

        return sourceId == 0 ? null : _objectTable.FindByEntityOrObjectId(sourceId) as IBattleChara;
    }
}
