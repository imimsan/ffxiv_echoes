using Dalamud.Plugin.Services;
using FfxivEchoes.Events;
using FfxivEchoes.SafeZone;
using FfxivEchoes.Triggers.Models;
using FfxivEchoes.Variables;

namespace FfxivEchoes.Actions.Handlers;

/// <summary>
/// 位置を変数として保存（P4 / SPEC.md §6.1 stored_position の入口）。
/// </summary>
/// <remarks>
/// 規約：
/// - <see cref="ActionDefinition.Label"/> を変数名として使用（必須）
/// - <see cref="ActionDefinition.SafeZone"/> が指定されていれば、その計算結果を保存
/// - 指定がなければ「自分の位置」を保存
///
/// 例：
///   { "type": "store_position", "label": "phase1_pos" }       → 自分位置を保存
///   { "type": "store_position", "label": "boss_at_p2",         → ボス位置を保存
///     "safe_zone": { "method": "fixed", "params": { "origin": "boss" } } }
/// </remarks>
public sealed class StorePositionHandler : IActionHandler
{
    public string Type => "store_position";

    private readonly SafeZoneEngine _engine;
    private readonly SafeZoneContextBuilder _contextBuilder;
    private readonly VariableStore _variables;
    private readonly IPluginLog _log;

    public StorePositionHandler(
        SafeZoneEngine engine, SafeZoneContextBuilder contextBuilder,
        VariableStore variables, IPluginLog log)
    {
        _engine = engine;
        _contextBuilder = contextBuilder;
        _variables = variables;
        _log = log;
    }

    public void Execute(ActionDefinition action, TriggerFiredEvent context)
    {
        var name = action.Label;
        if (string.IsNullOrEmpty(name))
        {
            _log.Warning("[FfxivEchoes] store_position に label（変数名）が指定されていません");
            return;
        }

        var ctx = _contextBuilder.Build(lastEvent: context.SourceEvent);
        if (action.SafeZone is { } sz)
        {
            var result = _engine.Calculate(sz, ctx);
            if (result is null)
            {
                _log.Debug("[FfxivEchoes] store_position：SafeZone 解決失敗（{Name}）", name);
                return;
            }
            _variables.StorePosition(name, result.WorldPosition);
            _log.Debug("[FfxivEchoes] store_position {Name} = {Pos}", name, result.WorldPosition);
            return;
        }

        // フォールバック：自分の位置
        _variables.StorePosition(name, ctx.SelfPosition);
        _log.Debug("[FfxivEchoes] store_position {Name} = self {Pos}", name, ctx.SelfPosition);
    }
}
