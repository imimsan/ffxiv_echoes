using FfxivEchoes.Triggers.Models;
using FfxivEchoes.Variables;

namespace FfxivEchoes.SafeZone.Presets;

/// <summary>
/// 履歴ベース安置（SPEC.md §6.1 stored_position）。
/// VariableStore に保存された Vector3 を読み出して返す。
/// params: { key: 変数名 }
/// </summary>
public sealed class StoredPositionPreset : ISafeZonePreset
{
    public string Method => "stored_position";

    private readonly VariableStore _variables;

    public StoredPositionPreset(VariableStore variables)
    {
        _variables = variables;
    }

    public SafeZoneResult? Calculate(SafeZoneCalculation calc, SafeZoneContext ctx)
    {
        var key = ParamHelper.GetString(calc.Params, "key") ?? "stored";
        var pos = _variables.GetPosition(key);
        if (pos is null)
        {
            return null;
        }
        return new SafeZoneResult(pos.Value, DirectionInfo.Compute(ctx.SelfPosition, pos.Value));
    }
}
