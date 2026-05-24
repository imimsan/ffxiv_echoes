using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FfxivEchoes.Capture;
using FfxivEchoes.Events;

namespace FfxivEchoes.SafeZone;

public sealed class SafeZoneContextBuilder
{
    private static readonly Vector3 DefaultArenaCenter = new(100f, 0f, 100f);

    private readonly IObjectTable _objectTable;
    private readonly IPartyList _partyList;

    public SafeZoneContextBuilder(IObjectTable objectTable, IPartyList partyList)
    {
        _objectTable = objectTable;
        _partyList = partyList;
    }

    public SafeZoneContext Build(IBattleChara? castActor = null, IGameEvent? lastEvent = null)
    {
        var localPlayer = _objectTable.LocalPlayer;
        var selfPos = localPlayer is not null
            ? new Vector3(localPlayer.Position.X, localPlayer.Position.Y, localPlayer.Position.Z)
            : Vector3.Zero;

        var npcById = new Dictionary<ulong, IBattleNpc>();
        var bossCandidates = new List<BossCandidate>();
        foreach (var obj in _objectTable)
        {
            if (obj is not IBattleNpc npc)
            {
                continue;
            }

            npcById[npc.GameObjectId] = npc;
            bossCandidates.Add(new BossCandidate(
                npc.GameObjectId,
                npc.Name.TextValue,
                npc.MaxHp,
                IsEnemy(npc)));
        }

        var selection = BossSelectionPolicy.Select(bossCandidates, castActor?.GameObjectId);
        var bosses = selection.Bosses
            .Where(b => npcById.ContainsKey(b.ObjectId))
            .Select(b => npcById[b.ObjectId])
            .ToArray();
        var boss = bosses.FirstOrDefault();

        var party = new List<IPlayerCharacter>();
        foreach (var member in _partyList)
        {
            foreach (var obj in _objectTable)
            {
                if (obj.GameObjectId == (ulong)member.EntityId && obj is IPlayerCharacter pc)
                {
                    party.Add(pc);
                    break;
                }
            }
        }

        if (localPlayer is not null && !party.Contains(localPlayer))
        {
            party.Insert(0, localPlayer);
        }

        var markers = BuildFieldMarkers();
        // 動的アリーナ中心：ジッタを避けるため、PT メンバー全員の bbox は使わない。
        // 優先度：ボス（1 体に絞れば動いても遥かに安定）→ デフォルト座標。
        // 校正済中心（StrategyProfile.ArenaCenterX/Z）はミニマップ描画側で
        // 上書きするのでここでは関与しない。
        Vector3 arenaCenter;
        if (boss is not null)
        {
            arenaCenter = new Vector3(boss.Position.X, boss.Position.Y, boss.Position.Z);
        }
        else if (castActor is not null)
        {
            arenaCenter = new Vector3(castActor.Position.X, castActor.Position.Y, castActor.Position.Z);
        }
        else
        {
            arenaCenter = DefaultArenaCenter;
        }

        return new SafeZoneContext(
            SelfPosition: selfPos,
            ArenaCenter: arenaCenter,
            Boss: boss,
            Party: party,
            FieldMarkers: markers,
            CastActor: castActor,
            LastEvent: lastEvent,
            Bosses: bosses);
    }

    private static bool IsEnemy(IBattleNpc npc)
    {
        try
        {
            return npc.BattleNpcKind != Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind.Pet;
        }
        catch
        {
            return npc.MaxHp > 0;
        }
    }

    private static Dictionary<string, Vector3> BuildFieldMarkers()
    {
        var markers = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
        var aliases = new (string Letter, string Key)[]
        {
            ("A", "marker_a"),
            ("B", "marker_b"),
            ("C", "marker_c"),
            ("D", "marker_d"),
            ("1", "marker_1"),
            ("2", "marker_2"),
            ("3", "marker_3"),
            ("4", "marker_4"),
        };

        foreach (var (letter, key) in aliases)
        {
            var pos = WaymarkProvider.TryGetPosition(letter);
            if (pos is null)
            {
                continue;
            }

            markers[key] = pos.Value;
            markers[letter] = pos.Value;
        }

        return markers;
    }
}
