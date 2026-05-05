using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;

namespace FfxivEchoes.SafeZone;

/// <summary>
/// 実行時の <see cref="SafeZoneContext"/> をスナップショットする。
/// </summary>
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

    /// <summary>
    /// 現在のフレームから SafeZoneContext を構築する。
    /// メインスレッド（IFramework.Update / Draw）から呼び出すこと。
    /// </summary>
    public SafeZoneContext Build(IBattleChara? castActor = null, IGameEvent? lastEvent = null)
    {
        var localPlayer = _objectTable.LocalPlayer;
        var selfPos = localPlayer is not null
            ? new Vector3(localPlayer.Position.X, localPlayer.Position.Y, localPlayer.Position.Z)
            : Vector3.Zero;

        IBattleNpc? boss = null;
        var bossThreat = 0;
        foreach (var obj in _objectTable)
        {
            if (obj is not IBattleNpc npc)
            {
                continue;
            }
            // 単純なヒューリスティック：MaxHp が一番大きい敵 NPC をボスとみなす
            if (npc.MaxHp > bossThreat)
            {
                boss = npc;
                bossThreat = (int)npc.MaxHp;
            }
        }

        var party = new List<IPlayerCharacter>();
        foreach (var member in _partyList)
        {
            // PartyMember.GameObject から IPlayerCharacter を解決
            foreach (var obj in _objectTable)
            {
                if (obj.GameObjectId == (ulong)member.EntityId && obj is IPlayerCharacter pc)
                {
                    party.Add(pc);
                    break;
                }
            }
        }

        // 自分が party に居なければ追加（ソロやインスタンス外）
        if (localPlayer is not null && !party.Contains(localPlayer))
        {
            party.Insert(0, localPlayer);
        }

        // F8 で FieldMarkerService が実装されたら差し替え
        var markers = new Dictionary<string, Vector3>();

        var arenaCenter = boss is not null
            ? new Vector3(boss.Position.X, boss.Position.Y, boss.Position.Z)
            : DefaultArenaCenter;

        return new SafeZoneContext(
            SelfPosition: selfPos,
            ArenaCenter: arenaCenter,
            Boss: boss,
            Party: party,
            FieldMarkers: markers,
            CastActor: castActor,
            LastEvent: lastEvent);
    }
}
