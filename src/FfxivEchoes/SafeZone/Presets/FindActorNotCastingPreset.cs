using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.SafeZone.Presets;

/// <summary>
/// キャスト中でないアクターを検索（SPEC.md §6.1）。
/// 楽園絶技の安置オブジェクト識別などに使う。
/// params: { name_contains: 名前フィルタ（任意）, actor_filter: enemy/ally/any }
/// </summary>
public sealed class FindActorNotCastingPreset : ISafeZonePreset
{
    public string Method => "find_actor_not_casting";

    private readonly IObjectTable _objectTable;

    public FindActorNotCastingPreset(IObjectTable objectTable)
    {
        _objectTable = objectTable;
    }

    public SafeZoneResult? Calculate(SafeZoneCalculation calc, SafeZoneContext ctx)
    {
        var nameContains = ParamHelper.GetString(calc.Params, "name_contains");
        var filter = ParamHelper.GetString(calc.Params, "actor_filter") ?? "any";

        IBattleChara? found = null;
        foreach (var obj in _objectTable)
        {
            if (obj is not IBattleChara chara)
            {
                continue;
            }
            if (!FilterAccepts(chara, filter))
            {
                continue;
            }
            if (nameContains is not null && !chara.Name.TextValue.Contains(nameContains))
            {
                continue;
            }
            // IBattleNpc.IsCasting でチェック（IPlayerCharacter は通常キャスト判定対象外）
            if (chara is IBattleNpc npc && npc.IsCasting)
            {
                continue;
            }
            found = chara;
            break;
        }

        if (found is null)
        {
            return null;
        }
        var pos = new Vector3(found.Position.X, found.Position.Y, found.Position.Z);
        return new SafeZoneResult(pos, DirectionInfo.Compute(ctx.SelfPosition, pos));
    }

    private static bool FilterAccepts(IBattleChara chara, string filter) => filter.ToLowerInvariant() switch
    {
        "enemy" => chara is IBattleNpc bn && bn.BattleNpcKind != Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind.Pet,
        "ally" => chara is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter,
        _ => true,
    };
}
