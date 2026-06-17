using System.Globalization;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;
using FfxivEchoes.SafeZone;
using FfxivEchoes.Triggers.Models;
using FfxivEchoes.Windows;

namespace FfxivEchoes.Actions.Handlers;

/// <summary>
/// 方角コール（SPEC.md §5.1 / §5.2 direction_call）。
/// </summary>
public sealed class DirectionCallHandler : IActionHandler
{
    public string Type => "direction_call";

    private readonly SafeZoneEngine _safeZoneEngine;
    private readonly SafeZoneContextBuilder _contextBuilder;
    private readonly Configuration _configuration;
    private readonly OverlayWindow _overlay;
    private readonly TtsHandler _tts;
    private readonly IChatGui _chatGui;
    private readonly IPluginLog _log;

    public DirectionCallHandler(
        SafeZoneEngine engine, SafeZoneContextBuilder builder,
        Configuration configuration, OverlayWindow overlay,
        TtsHandler tts, IChatGui chatGui, IPluginLog log)
    {
        _safeZoneEngine = engine;
        _contextBuilder = builder;
        _configuration = configuration;
        _overlay = overlay;
        _tts = tts;
        _chatGui = chatGui;
        _log = log;
    }

    public void Execute(ActionDefinition action, TriggerFiredEvent context)
    {
        if (action.SafeZone is null)
        {
            _log.Warning("[FfxivEchoes] direction_call に safe_zone 指定がありません");
            return;
        }

        var ctx = _contextBuilder.Build(lastEvent: context.SourceEvent);
        var result = _safeZoneEngine.Calculate(action.SafeZone, ctx);
        if (result?.FromPlayer is null)
        {
            _log.Debug("[FfxivEchoes] direction_call の SafeZone 解決失敗");
            return;
        }

        // output_format.direction_style があれば action.Format より優先（サンプルが safe_zone 側で文面指定するため）。
        var outputFormat = action.SafeZone.OutputFormat;
        var format = (outputFormat?.DirectionStyle ?? action.Format ?? "cardinal_jp").ToLowerInvariant();
        var directionText = FormatDirection(result.FromPlayer, format);

        // tts_template が指定されていれば ${direction_clock} 等を補間して文面とする。
        // 無ければ従来どおり action.Text + 方角。
        string fullText;
        if (!string.IsNullOrEmpty(outputFormat?.TtsTemplate))
        {
            fullText = ApplyTemplate(outputFormat!.TtsTemplate!, result.FromPlayer);
        }
        else
        {
            fullText = string.IsNullOrEmpty(action.Text) ? directionText : $"{action.Text} {directionText}";
        }

        if (action.Tts ?? true)
        {
            _tts.Execute(new ActionDefinition
            {
                Type = "tts",
                Text = fullText,
                Voice = action.Voice,
                Volume = action.Volume,
                Rate = action.Rate,
                Priority = action.Priority,
            }, context);
        }

        if (action.Overlay ?? false)
        {
            _overlay.AddText(fullText, action.Duration ?? 4.0, action.Color, action.Size);
        }
        else
        {
            _chatGui.Print($"[Echoes] {fullText}");
        }
    }

    /// <summary>
    /// output_format.tts_template の <c>${...}</c> トークンを方角情報で補間する。
    /// 未知トークンは原文のまま残す（リテラル ${...} になっても安全側）。
    /// 対応トークン：direction_clock / direction_cardinal / direction_cardinal_jp /
    /// direction_relative_jp / direction_deg。
    /// </summary>
    public static string ApplyTemplate(string template, DirectionInfo info)
    {
        return template
            .Replace("${direction_clock}", info.DirectionClock.ToString("0.#", CultureInfo.InvariantCulture))
            .Replace("${direction_cardinal_jp}", CardinalJp(info.DirectionCardinal))
            .Replace("${direction_relative_jp}", RelativeJp(info.DirectionDeg))
            .Replace("${direction_cardinal}", info.DirectionCardinal)
            .Replace("${direction_deg}", info.DirectionDeg.ToString("0", CultureInfo.InvariantCulture));
    }

    private static string FormatDirection(DirectionInfo info, string format) => format switch
    {
        "cardinal" => info.DirectionCardinal,
        "cardinal_jp" => CardinalJp(info.DirectionCardinal),
        "clock" => $"{info.DirectionClock:0.#}時方向",
        "degrees" => $"{info.DirectionDeg.ToString("0", CultureInfo.InvariantCulture)}度",
        "relative_jp" => RelativeJp(info.DirectionDeg),
        _ => CardinalJp(info.DirectionCardinal),
    };

    private static string CardinalJp(string c) => c switch
    {
        "N" => "北",
        "NE" => "北東",
        "E" => "東",
        "SE" => "南東",
        "S" => "南",
        "SW" => "南西",
        "W" => "西",
        "NW" => "北西",
        _ => c,
    };

    private static string RelativeJp(float deg)
    {
        // 0=正面、180=背面（プレイヤー前方を北と仮定して簡易）
        if (deg < 22.5f || deg >= 337.5f) return "正面";
        if (deg < 67.5f) return "右前";
        if (deg < 112.5f) return "右";
        if (deg < 157.5f) return "右後";
        if (deg < 202.5f) return "背面";
        if (deg < 247.5f) return "左後";
        if (deg < 292.5f) return "左";
        return "左前";
    }
}
