using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Triggers.Matching;

/// <summary>
/// マッチ条件の "target" 文字列をパーティ構成に対して評価する（SPEC.md §4.3）。
/// </summary>
/// <remarks>
/// 解決可能な値（M6 範囲）：self / other / any / party / tank / mt / st /
/// healer / h1 / h2 / dps / melee / ranged / caster。
/// marker_a〜h / marker_1〜8 はフィールドマーカー機構（F8）で対応するため
/// M6 では常に false を返す。
/// </remarks>
public sealed class TargetResolver
{
    private readonly IPartyList _partyList;
    private readonly IPlayerState _playerState;

    public TargetResolver(IPartyList partyList, IPlayerState playerState)
    {
        _partyList = partyList;
        _playerState = playerState;
    }

    public bool Matches(uint targetActorId, TargetSpec? spec)
    {
        if (spec is null)
        {
            return true;
        }
        foreach (var v in spec.Values)
        {
            if (Matches(targetActorId, v))
            {
                return true;
            }
        }
        return false;
    }

    public bool Matches(uint targetActorId, string targetSpec)
    {
        if (string.IsNullOrEmpty(targetSpec))
        {
            return true;
        }

        var lower = targetSpec.ToLowerInvariant();
        return lower switch
        {
            "any" => true,
            "self" => IsSelf(targetActorId),
            "other" => !IsSelf(targetActorId),
            "party" => IsInParty(targetActorId),
            "tank" => IsRole(targetActorId, 1),
            "healer" => IsRole(targetActorId, 4),
            "dps" => IsRole(targetActorId, 2) || IsRole(targetActorId, 3),
            "melee" => IsRole(targetActorId, 2),
            "ranged" or "caster" => IsRole(targetActorId, 3),
            "mt" => IsRoleAtIndex(targetActorId, 1, 0),
            "st" => IsRoleAtIndex(targetActorId, 1, 1),
            "h1" => IsRoleAtIndex(targetActorId, 4, 0),
            "h2" => IsRoleAtIndex(targetActorId, 4, 1),
            // marker_* は F8 の field marker サポートで実装
            var s when s.StartsWith("marker_") => false,
            _ => false,
        };
    }

    private bool IsSelf(uint actorId)
    {
        if (!_playerState.IsLoaded)
        {
            return false;
        }
        return actorId == _playerState.EntityId;
    }

    private bool IsInParty(uint actorId)
    {
        foreach (var member in _partyList)
        {
            if ((uint)member.EntityId == actorId)
            {
                return true;
            }
        }
        return IsSelf(actorId);
    }

    private bool IsRole(uint actorId, byte role)
    {
        foreach (var member in _partyList)
        {
            if ((uint)member.EntityId != actorId)
            {
                continue;
            }
            return member.ClassJob.Value.Role == role;
        }

        // PartyList に居なくても、自分自身ならジョブから判定
        if (IsSelf(actorId) && _playerState.IsLoaded)
        {
            return _playerState.ClassJob.Value.Role == role;
        }
        return false;
    }

    private bool IsRoleAtIndex(uint actorId, byte role, int index)
    {
        var ordered = new List<uint>();
        foreach (var member in _partyList)
        {
            if (member.ClassJob.Value.Role == role)
            {
                ordered.Add((uint)member.EntityId);
            }
        }
        if (index < 0 || index >= ordered.Count)
        {
            return false;
        }
        return ordered[index] == actorId;
    }
}
