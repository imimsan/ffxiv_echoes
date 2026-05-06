using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
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

        var markers = new Dictionary<string, Vector3>();
        var arenaCenter = DefaultArenaCenter;

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
}
