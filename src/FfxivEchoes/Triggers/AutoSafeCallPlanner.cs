using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Triggers;

/// <summary>
/// AoE / 安置の自動アナウンス計算 helper。
/// </summary>
/// <remarks>
/// <para>
/// 設計負債：本クラスは現状 <c>static</c> で <see cref="Dictionary"/> を共有 state として
/// 持つ。これは <c>coding-rules.md</c> の「サービスロケータパターン禁止」の境界グレーで、
/// 完全な DI 化（callsite 約 30 箇所の instance 化）は侵襲的なので保留中。
/// 代わりに：
/// </para>
/// <list type="bullet">
///   <item>セッターを <see cref="Initialize"/> に変えて二重初期化と null 注入を fail-fast。</item>
///   <item>使用側は <c>Dictionary?</c> で null safe にアクセス。</item>
///   <item>将来の DI 化 TODO は <c>roadmap.md</c> 由来の改善項目として保留。</item>
/// </list>
/// </remarks>
public static class AutoSafeCallPlanner
{
    private const float MaxCircleSafeCallRadius = 15f;

    private static readonly HashSet<uint> HalfArenaCastIds = new()
    {
        0x179C,
        0x189F,
    };

    private static SafeCallDictionary? _dictionary;

    /// <summary>
    /// 現在保持している辞書（Plugin.cs 起動時に <see cref="Initialize"/> で注入される）。
    /// 初期化前は null（その場合 raid-wide 判定は false 扱い、安全側に倒す）。
    /// 読み取り専用：書き換えは <see cref="Initialize"/> 経由のみ許可（fail-fast / 二重初期化検出）。
    /// </summary>
    public static SafeCallDictionary? Dictionary => _dictionary;

    /// <summary>
    /// プラグイン起動時に 1 回だけ呼ばれる初期化メソッド。
    /// 二重初期化はプラグイン再ロード以外では発生しないはずなので、ログを出して上書きする。
    /// </summary>
    public static void Initialize(SafeCallDictionary dictionary, IPluginLog? log = null)
    {
        if (dictionary is null) throw new ArgumentNullException(nameof(dictionary));
        if (_dictionary is not null && !ReferenceEquals(_dictionary, dictionary))
        {
            log?.Warning("[FfxivEchoes] AutoSafeCallPlanner.Dictionary 二重初期化（リロード推定）");
        }
        _dictionary = dictionary;
    }

    /// <summary>
    /// テスト用ヘルパ：テスト間のクリーンスレートを保証するため辞書をクリア。
    /// プロダクションコードからは呼ばないこと（プラグインの寿命中は辞書は維持される）。
    /// </summary>
    internal static void ResetForTesting()
    {
        _dictionary = null;
    }

    /// <summary>
    /// 辞書に raid-wide マーク済みのキャストかどうかを判定。
    /// 該当する場合、ミニマップに範囲を描いても無意味（回避不可）なので呼び出し側でスキップする。
    /// </summary>
    public static bool IsRaidWide(uint actionId, string? castName)
    {
        return IsGlobalRaidWideSuppressionEntry(Dictionary?.Lookup(actionId, castName));
    }

    public static bool IsRaidWide(TriggerFile? file, uint actionId, string? castName)
    {
        return FindRaidWideMarker(file, actionId, castName) is not null ||
               IsRaidWide(actionId, castName);
    }

    public static RaidWideMarker? FindRaidWideMarker(TriggerFile? file, uint actionId, string? castName)
    {
        if (file?.RaidWideMarkers is null || file.RaidWideMarkers.Count == 0)
        {
            return null;
        }

        foreach (var marker in file.RaidWideMarkers)
        {
            // Id を持つ marker は Id 完全一致のみで判定する。Id 不一致時に名前へ fallback すると、
            // 同名の別 cast_id（例: ボス移動技 0x28D3「ぶっとびテレポ」の marker が、同名の実攻撃
            // 0x28D4 の安置 arena_view を巻き込んで誤抑止する）を raid-wide と誤判定してしまう。
            if (!string.IsNullOrWhiteSpace(marker.Id) &&
                AoeResolver.TryParseCastId(marker.Id, out var markerId))
            {
                if (markerId == actionId)
                {
                    return marker;
                }
                continue;
            }

            // Id を持たない marker のみ、後方互換として名前一致を許可する。
            if (!string.IsNullOrWhiteSpace(marker.Name) &&
                !string.IsNullOrWhiteSpace(castName) &&
                string.Equals(marker.Name, castName, StringComparison.OrdinalIgnoreCase))
            {
                return marker;
            }
        }

        return null;
    }

    public static bool IsGlobalRaidWideSuppressionEntry(SafeCallDictionary.DictionaryEntry? entry)
    {
        if (entry?.RaidWide != true)
        {
            return false;
        }

        // 旧版は HP 相関の自動検出をグローバル辞書へ保存していたため、別コンテンツまで
        // AoE が消える。互換用の手動グローバル指定だけ残し、自動検出分は抑止に使わない。
        return !string.Equals(entry.Source, "hp_correlation", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldSuppressMinimap(MatchCondition? match)
    {
        return ShouldSuppressMinimap(match, IsRaidWide);
    }

    public static bool ShouldSuppressMinimap(TriggerFile? file, MatchCondition? match)
    {
        return ShouldSuppressMinimap(match, (id, name) => IsRaidWide(file, id, name));
    }

    public static bool ShouldSuppressMinimap(MatchCondition? match, Func<uint, string?, bool> isRaidWide)
    {
        if (match is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(match.CastId) &&
            AoeResolver.TryParseCastId(match.CastId, out var castId) &&
            isRaidWide(castId, match.CastName))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(match.ActionId) &&
            AoeResolver.TryParseCastId(match.ActionId, out var actionId) &&
            isRaidWide(actionId, match.ActionName))
        {
            return true;
        }

        return false;
    }

    public static bool ShouldSuppressMinimap(IGameEvent? sourceEvent)
    {
        return ShouldSuppressMinimap(sourceEvent, IsRaidWide);
    }

    public static bool ShouldSuppressMinimap(TriggerFile? file, IGameEvent? sourceEvent)
    {
        return ShouldSuppressMinimap(sourceEvent, (id, name) => IsRaidWide(file, id, name));
    }

    public static bool ShouldSuppressMinimap(IGameEvent? sourceEvent, Func<uint, string?, bool> isRaidWide)
    {
        return sourceEvent switch
        {
            CastStartedEvent ev => isRaidWide(ev.CastActionId, ev.CastActionName),
            CastCompletedEvent ev => isRaidWide(ev.CastActionId, ev.CastActionName),
            CastCanceledEvent ev => isRaidWide(ev.CastActionId, ev.CastActionName),
            ActionUsedEvent ev => isRaidWide(ev.ActionId, ev.ActionName),
            _ => false,
        };
    }

    public static IReadOnlyList<ActionDefinition> RemoveMinimapActionsForRaidWideSource(
        IReadOnlyList<ActionDefinition> actions,
        IGameEvent? sourceEvent)
    {
        return RemoveMinimapActionsForRaidWideSource(actions, sourceEvent, IsRaidWide);
    }

    public static IReadOnlyList<ActionDefinition> RemoveMinimapActionsForRaidWideSource(
        TriggerFile? file,
        IReadOnlyList<ActionDefinition> actions,
        IGameEvent? sourceEvent)
    {
        return RemoveMinimapActionsForRaidWideSource(actions, sourceEvent,
            (id, name) => IsRaidWide(file, id, name));
    }

    public static IReadOnlyList<ActionDefinition> RemoveMinimapActionsForRaidWideSource(
        IReadOnlyList<ActionDefinition> actions,
        IGameEvent? sourceEvent,
        Func<uint, string?, bool> isRaidWide)
    {
        if (!ShouldSuppressMinimap(sourceEvent, isRaidWide))
        {
            return actions;
        }

        return RemoveMinimapActions(actions);
    }

    public static IReadOnlyList<ActionDefinition> RemoveMinimapActionsForRaidWideMatch(
        IReadOnlyList<ActionDefinition> actions,
        MatchCondition? match)
    {
        return RemoveMinimapActionsForRaidWideMatch(actions, match, IsRaidWide);
    }

    public static IReadOnlyList<ActionDefinition> RemoveMinimapActionsForRaidWideMatch(
        TriggerFile? file,
        IReadOnlyList<ActionDefinition> actions,
        MatchCondition? match)
    {
        return RemoveMinimapActionsForRaidWideMatch(actions, match,
            (id, name) => IsRaidWide(file, id, name));
    }

    public static IReadOnlyList<ActionDefinition> RemoveMinimapActionsForRaidWideMatch(
        IReadOnlyList<ActionDefinition> actions,
        MatchCondition? match,
        Func<uint, string?, bool> isRaidWide)
    {
        if (!ShouldSuppressMinimap(match, isRaidWide))
        {
            return actions;
        }

        return RemoveMinimapActions(actions);
    }

    private static IReadOnlyList<ActionDefinition> RemoveMinimapActions(IReadOnlyList<ActionDefinition> actions)
    {
        var filtered = new List<ActionDefinition>(actions.Count);
        var removed = false;
        foreach (var action in actions)
        {
            if (string.Equals(action.Type, "arena_view", StringComparison.OrdinalIgnoreCase))
            {
                // user layout（手動で AoE / 散開ポジ / オブジェクト / 安置を定義した）arena_view は
                // raid-wide マークがあっても残す。「全体攻撃マークしてる cast に紐づけて手動で
                // ミニマップを描く」のは正当な用途（例：raid-wide だけど位置取りたい技で全員集合等）。
                var hasUserLayout =
                    action.SafeZone is not null ||
                    (action.StrategyPositions?.Count ?? 0) > 0 ||
                    (action.ObjectMarkers?.Count ?? 0) > 0 ||
                    (action.AoeZones?.Count ?? 0) > 0;
                if (!hasUserLayout)
                {
                    removed = true;
                    continue;
                }
            }

            filtered.Add(action);
        }

        return removed ? filtered : actions;
    }

    /// <summary>辞書に登録された AoE 半径オーバーライド（メートル）。無ければ null。</summary>
    public static double? RadiusOverride(uint actionId, string? castName)
    {
        return Dictionary?.Lookup(actionId, castName)?.AoeRadiusM;
    }

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
            if (IsGlobalRaidWideSuppressionEntry(dictEntry))
            {
                return null;
            }

            if (dictEntry.RaidWide)
            {
                // 旧 hp_correlation 自動登録は raid-wide 抑止としては無視する。
                // safe call としても意味を持たないので、通常の Lumina / 名前ルールに続行。
            }
            else
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

        if (aoe.CastType is 6 or 7 or 10)
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

        if (aoe.CastType is 3 or 4 or 11 or 12 or 13 && aoe.Radius > 0)
        {
            var fanDeg = aoe.CastType is 4 or 12
                ? 30.0
                : 90.0;
            return new AutoSafeCall(
                Gimmick: "cone",
                Callout: $"AoE: {label}",
                TtsText: string.Empty,
                FieldMarkerColor: "#FF6464",
                IsEstimate: false,
                FanDeg: fanDeg);
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
