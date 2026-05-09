using System;
using System.Collections.Generic;

namespace FfxivEchoes.Triggers;

public static class AutoSafeCallPlanner
{
    private const float MaxCircleSafeCallRadius = 15f;

    private static readonly HashSet<uint> HalfArenaCastIds = new()
    {
        0x179C,
        0x189F,
    };

    public static SafeCallDictionary? Dictionary { get; set; }

    public static AutoSafeCall? CreateKnownByName(string castName)
    {
        return CreateKnown(0, castName);
    }

    public static AutoSafeCall? CreateKnown(uint actionId, string castName)
    {
        if (string.IsNullOrWhiteSpace(castName))
        {
            castName = actionId == 0 ? string.Empty : $"0x{actionId:X}";
        }

        var dictEntry = Dictionary?.Lookup(actionId, castName);
        if (dictEntry is not null)
        {
            var callout = string.IsNullOrEmpty(dictEntry.Callout)
                ? castName
                : dictEntry.Callout.Contains("{name}", StringComparison.OrdinalIgnoreCase)
                    ? dictEntry.Callout.Replace("{name}", castName)
                    : $"{dictEntry.Callout}・{castName}";
            return new AutoSafeCall(
                Gimmick: dictEntry.Gimmick,
                Callout: callout,
                TtsText: dictEntry.Tts ?? dictEntry.Callout ?? "回避",
                FieldMarkerColor: "#FF6464",
                IsEstimate: dictEntry.Source == "multi_cast_detected" || dictEntry.Source == "omen",
                FanDeg: dictEntry.FanDeg);
        }

        if (HalfArenaCastIds.Contains(actionId) ||
            ContainsAny(castName,
                "ヒートウィング",
                "Heat Wing",
                "カータライズ",
                "Cauterize",
                "繝偵・繝医え繧｣繝ｳ繧ｰ",
                "繧ｫ繝ｼ繧ｿ繝ｩ繧､繧ｺ"))
        {
            return new AutoSafeCall(
                Gimmick: "half_plane",
                Callout: $"半面回避・{castName}",
                TtsText: "半面回避",
                FieldMarkerColor: "#FF6464",
                IsEstimate: false,
                FanDeg: 180);
        }

        return null;
    }

    public static AutoSafeCall? Create(AoeResolver.AoeInfo aoe, string castName)
    {
        var label = string.IsNullOrWhiteSpace(castName) ? "AoE" : castName;

        if (aoe.OmenId != 0)
        {
            var omenGimmick = AoeResolver.GuessGimmickByOmen(aoe.OmenId);
            if (omenGimmick is not null)
            {
                return new AutoSafeCall(
                    Gimmick: omenGimmick,
                    Callout: $"{CalloutFor(omenGimmick)}・{label}",
                    TtsText: CalloutFor(omenGimmick),
                    FieldMarkerColor: "#FF6464",
                    IsEstimate: false,
                    FanDeg: omenGimmick == "cone" ? 90 : null);
            }
        }

        if (aoe.CastType == 6)
        {
            return new AutoSafeCall(
                Gimmick: "outer_ring",
                    Callout: $"内側安置：{label}",
                    TtsText: "内側安置",
                FieldMarkerColor: "#FFB84D",
                IsEstimate: false,
                FanDeg: null);
        }

        if ((aoe.CastType == 2 || aoe.CastType == 5) && aoe.Radius < MaxCircleSafeCallRadius)
        {
            return new AutoSafeCall(
                Gimmick: "inner_circle",
                Callout: $"外周安置：{label}",
                TtsText: "外周安置",
                FieldMarkerColor: "#FF6464",
                IsEstimate: false,
                FanDeg: null);
        }

        if (aoe.CastType == 3)
        {
            return new AutoSafeCall(
                Gimmick: "cone",
                Callout: $"扇形回避：{label}",
                TtsText: "扇形回避",
                FieldMarkerColor: "#FF6464",
                IsEstimate: true,
                FanDeg: 90);
        }

        if (aoe.CastType == 4)
        {
            return new AutoSafeCall(
                Gimmick: "cone",
                Callout: $"直線回避：{label}",
                TtsText: "直線回避",
                FieldMarkerColor: "#FF6464",
                IsEstimate: true,
                FanDeg: 30);
        }

        return null;
    }

    public static AutoSafeCall? CreateVisual(AoeResolver.AoeInfo aoe, string castName)
    {
        var safeCall = Create(aoe, castName);
        if (safeCall is not null)
        {
            return safeCall;
        }

        var label = string.IsNullOrWhiteSpace(castName) ? "AoE" : castName;
        if ((aoe.CastType == 2 || aoe.CastType == 5) && aoe.Radius > 0)
        {
            return new AutoSafeCall(
                Gimmick: "inner_circle",
                Callout: $"AoE: {label}",
                TtsText: string.Empty,
                FieldMarkerColor: "#FF6464",
                IsEstimate: false,
                FanDeg: null);
        }

        return null;
    }

    private static string CalloutFor(string gimmick) => gimmick switch
    {
        "outer_ring" => "内側安置",
        "inner_circle" => "外周回避",
        "cone" => "扇形回避",
        "half_plane" => "半面回避",
        _ => "回避",
    };

    private static bool ContainsAny(string value, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (value.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

public sealed record AutoSafeCall(
    string Gimmick,
    string Callout,
    string TtsText,
    string FieldMarkerColor,
    bool IsEstimate,
    double? FanDeg);
