using System.Numerics;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;
using FfxivEchoes.SafeZone;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Actions.Handlers;

/// <summary>
/// 安置内/外のフィードバック音（SPEC.md §5.1 proximity_feedback）。
/// 簡易実装：アクション実行時の単発判定で in_sound または out_sound を再生。
/// 連続評価（戦闘中ずっと監視）は将来 ProximityMonitor として独立実装予定。
/// </summary>
public sealed class ProximityFeedbackHandler : IActionHandler
{
    public string Type => "proximity_feedback";

    private readonly SafeZoneEngine _engine;
    private readonly SafeZoneContextBuilder _contextBuilder;
    private readonly WavHandler _wavHandler;
    private readonly IChatGui _chatGui;
    private readonly IPluginLog _log;

    public ProximityFeedbackHandler(
        SafeZoneEngine engine, SafeZoneContextBuilder contextBuilder,
        WavHandler wavHandler, IChatGui chatGui, IPluginLog log)
    {
        _engine = engine;
        _contextBuilder = contextBuilder;
        _wavHandler = wavHandler;
        _chatGui = chatGui;
        _log = log;
    }

    public void Execute(ActionDefinition action, TriggerFiredEvent context)
    {
        if (action.SafeZone is null)
        {
            _log.Warning("[FfxivEchoes] proximity_feedback に safe_zone 指定がありません");
            return;
        }

        var ctx = _contextBuilder.Build(lastEvent: context.SourceEvent);
        var result = _engine.Calculate(action.SafeZone, ctx);
        if (result is null)
        {
            return;
        }

        var dx = ctx.SelfPosition.X - result.WorldPosition.X;
        var dz = ctx.SelfPosition.Z - result.WorldPosition.Z;
        var distance = System.MathF.Sqrt(dx * dx + dz * dz);
        var tolerance = (float)(action.Tolerance ?? 2.0);
        var isInSafe = distance <= tolerance;

        if (action.ShowDistance == true)
        {
            _chatGui.Print($"[Echoes] 安置まで {distance:0.0}m{(isInSafe ? "（安置内）" : string.Empty)}");
        }

        var soundFile = isInSafe ? action.InSound : action.OutSound;
        if (!string.IsNullOrEmpty(soundFile))
        {
            _wavHandler.Execute(new ActionDefinition
            {
                Type = "wav",
                File = soundFile,
                Volume = action.Volume,
            }, context);
        }
    }
}
