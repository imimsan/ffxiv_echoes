using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

namespace FfxivEchoes.Utils;

public static class ObjectTableLookup
{
    public static IGameObject? FindByEntityOrObjectId(this IObjectTable objectTable, uint id)
    {
        if (id == 0)
        {
            return null;
        }

        return objectTable.SearchByEntityId(id) ?? objectTable.SearchById(id);
    }
}
