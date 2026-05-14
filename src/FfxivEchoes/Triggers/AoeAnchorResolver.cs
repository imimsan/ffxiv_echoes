using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FfxivEchoes.Capture;
using FfxivEchoes.Events;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Triggers;

/// <summary>
/// <see cref="StrategyAoeZone.Anchor"/> 文字列を解釈して描画基準点を解決する純粋ロジック。
/// Splatoon の <c>refActorType</c> + <c>refActorComparisonType</c> 評価に相当。
/// </summary>
/// <remarks>
/// <see cref="ActorTrackedAoeService"/> から呼ばれる。anchor = source_actor / matched_object /
/// each_matched_object のときは <see cref="AnchorResult.ActorId"/> を返し、呼び出し側で per-frame
/// に actor.Position を引いて zone offset を適用する形を取る（毎フレーム actor 追従のため）。
/// anchor = static / waymark のときは <see cref="AnchorResult.StaticWorldPos"/> を返して固定位置
/// として扱う。
/// </remarks>
public static class AoeAnchorResolver
{
    /// <summary>1 つの zone を解決した結果。each_matched_object のときは複数返す。</summary>
    /// <param name="ActorId">非 null なら毎フレームこの actor の位置 + zone offset で描画。</param>
    /// <param name="StaticWorldPos">非 null なら固定座標。両方 null は無効。</param>
    public sealed record AnchorResult(uint? ActorId, Vector3? StaticWorldPos);

    /// <summary>
    /// zone と発火元イベントから描画基準点を 1 件以上解決する。
    /// 解決できない（actor 不在 / waymark 未設置など）場合は空配列。
    /// </summary>
    public static IReadOnlyList<AnchorResult> Resolve(
        StrategyAoeZone zone,
        Vector3 arenaCenter,
        IGameEvent? sourceEvent,
        IObjectTable objectTable)
    {
        var anchor = (zone.Anchor ?? "static").Trim().ToLowerInvariant();
        var eventAnchors = ResolveEventStaticAnchors(zone, arenaCenter, sourceEvent);
        if (eventAnchors.Count > 0)
        {
            return eventAnchors;
        }

        switch (anchor)
        {
            case "source_actor":
            {
                var src = ResolveSourceActorId(sourceEvent);
                return src == 0
                    ? Array.Empty<AnchorResult>()
                    : new[] { new AnchorResult(src, null) };
            }

            case "matched_object":
            {
                if (zone.ActorMatcher is null)
                {
                    // matcher 未指定 ＋ matched_object 指定 → 発火イベントの第 1 候補
                    var fallback = ResolveSourceActorId(sourceEvent);
                    return fallback == 0
                        ? Array.Empty<AnchorResult>()
                        : new[] { new AnchorResult(fallback, null) };
                }
                foreach (var actor in objectTable)
                {
                    if (MatchesActor(zone.ActorMatcher, actor))
                    {
                        return new[] { new AnchorResult((uint)actor.GameObjectId, null) };
                    }
                }
                return Array.Empty<AnchorResult>();
            }

            case "each_matched_object":
            {
                if (zone.ActorMatcher is null)
                {
                    // matcher 必須。指定無しは無効動作（ログレベルでも安全寄りに空返し）
                    return Array.Empty<AnchorResult>();
                }
                var list = new List<AnchorResult>(4);
                foreach (var actor in objectTable)
                {
                    if (MatchesActor(zone.ActorMatcher, actor))
                    {
                        list.Add(new AnchorResult((uint)actor.GameObjectId, null));
                        if (!zone.ActorMatcher.MatchAll && list.Count == 1) break;
                    }
                }
                return list;
            }

            case "waymark":
            {
                var wm = WaymarkProvider.TryGetPosition(zone.AnchorWaymark);
                return wm is { } pos
                    ? new[] { new AnchorResult(null, pos) }
                    : Array.Empty<AnchorResult>();
            }

            case "static":
            default:
                return new[] { new AnchorResult(null, arenaCenter) };
        }
    }

    public static IReadOnlyList<AnchorResult> ResolveEventStaticAnchors(
        StrategyAoeZone zone,
        Vector3 arenaCenter,
        IGameEvent? sourceEvent)
    {
        var anchor = (zone.Anchor ?? "static").Trim().ToLowerInvariant();
        return anchor switch
        {
            "matched_object" => ResolveMatchedObjectEvent(zone, sourceEvent, firstOnly: true),
            "each_matched_object" => ResolveMatchedObjectEvent(zone, sourceEvent, firstOnly: false),
            _ => Array.Empty<AnchorResult>(),
        };
    }

    private static IReadOnlyList<AnchorResult> ResolveMatchedObjectEvent(
        StrategyAoeZone zone,
        IGameEvent? sourceEvent,
        bool firstOnly)
    {
        switch (sourceEvent)
        {
            case ObjectAppearedEvent obj when MatchesObjectEvent(zone.ActorMatcher, obj.ObjectName, obj.DataId, obj.ObjectId):
                return new[] { new AnchorResult(null, obj.Position) };

            case ObjectGroupAppearedEvent group when MatchesObjectEvent(zone.ActorMatcher, group.ObjectName, group.DataId, null):
            {
                if (group.Positions.Count == 0)
                {
                    return Array.Empty<AnchorResult>();
                }

                if (firstOnly)
                {
                    return new[] { new AnchorResult(null, group.Positions[0]) };
                }

                var anchors = new List<AnchorResult>(group.Positions.Count);
                foreach (var pos in group.Positions)
                {
                    anchors.Add(new AnchorResult(null, pos));
                }
                return anchors;
            }

            default:
                return Array.Empty<AnchorResult>();
        }
    }

    /// <summary>
    /// 発火イベントから「ソースアクター（cast 元 / status target / object 出現）」の id を取り出す。
    /// </summary>
    public static uint ResolveSourceActorId(IGameEvent? ev) => ev switch
    {
        CastStartedEvent c => c.SourceId,
        CastCompletedEvent c => c.SourceId,
        CastCanceledEvent c => c.SourceId,
        ActionUsedEvent a => a.SourceId,
        StatusGainedEvent s => s.TargetId,    // status は target に付くので target を起点
        StatusLostEvent s => s.TargetId,
        ObjectAppearedEvent o => o.EntityId ?? o.ObjectId,
        HpChangedEvent h => h.ActorId,
        _ => 0u,
    };

    /// <summary>
    /// <see cref="ActorMatcher"/> の各フィールドを AND 評価。
    /// VFX path / ObjectEffect は専用キャプチャ未実装のため現状は無視（後続 Phase で配線）。
    /// </summary>
    public static bool MatchesActor(ActorMatcher m, IGameObject actor)
    {
        // ── 数値 ID 系（最安）─────────────────────────
        if (m.ObjectId is { } oid && oid != 0 && (uint)actor.GameObjectId != oid) return false;
        if (m.DataId is { } did && did != 0 && actor.BaseId != did) return false;

        // BattleChara 限定属性
        if (actor is IBattleNpc bnpc)
        {
            if (m.NpcNameId is { } nid && nid != 0 && bnpc.NameId != nid) return false;
            if (m.AliveOnly && bnpc.CurrentHp == 0) return false;
        }
        else
        {
            // BattleNpc 専用フィールドが指定されていて非 BattleNpc だった場合はミスマッチ
            if (m.NpcNameId is { } nid && nid != 0) return false;
            if (m.NpcBaseId is { } _) return false;
            if (m.ModelCharaId is { } _) return false;
        }

        if (m.TargetableOnly && !actor.IsTargetable) return false;

        // ── 名前 ─────────────────────────────────────
        if (!string.IsNullOrEmpty(m.Name))
        {
            var actorName = actor.Name.TextValue;
            if (string.IsNullOrEmpty(actorName)) return false;
            var mode = (m.NameMatch ?? "exact").Trim().ToLowerInvariant();
            var matched = mode switch
            {
                "contains" => actorName.Contains(m.Name, StringComparison.OrdinalIgnoreCase),
                "startswith" => actorName.StartsWith(m.Name, StringComparison.OrdinalIgnoreCase),
                "regex" => SafeRegexMatch(m.Name, actorName),
                _ => string.Equals(actorName, m.Name, StringComparison.OrdinalIgnoreCase),
            };
            if (!matched) return false;
        }

        return true;
    }

    private static bool MatchesObjectEvent(ActorMatcher? matcher, string objectName, uint dataId, uint? objectId)
    {
        if (matcher is null)
        {
            return true;
        }

        if (matcher.ObjectId is { } oid && oid != 0)
        {
            if (objectId is null || oid != objectId.Value) return false;
        }
        if (matcher.DataId is { } did && did != 0 && did != dataId) return false;

        if (!string.IsNullOrEmpty(matcher.Name))
        {
            if (string.IsNullOrEmpty(objectName)) return false;
            var mode = (matcher.NameMatch ?? "exact").Trim().ToLowerInvariant();
            var matched = mode switch
            {
                "contains" => objectName.Contains(matcher.Name, StringComparison.OrdinalIgnoreCase),
                "startswith" => objectName.StartsWith(matcher.Name, StringComparison.OrdinalIgnoreCase),
                "regex" => SafeRegexMatch(matcher.Name, objectName),
                _ => string.Equals(objectName, matcher.Name, StringComparison.OrdinalIgnoreCase),
            };
            if (!matched) return false;
        }

        return true;
    }

    /// <summary>
    /// 例外を握り潰す regex マッチ（不正パターンで戦闘中にクラッシュさせない）。
    /// </summary>
    private static bool SafeRegexMatch(string pattern, string input)
    {
        try
        {
            return Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
