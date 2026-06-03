using System;
using System.Collections.Generic;
using System.Linq;

namespace FfxivEchoes.Recording;

/// <summary>
/// 録画を「プル種別（前半フルプル / 後半頭出し練習プル / 各フェーズ start）」に分離した集計結果。
/// </summary>
/// <remarks>
/// 同一ゾーンの録画でも、フェーズ別に頭出し練習したプルは「その技から戦闘開始（相対秒 0 再起点）」
/// となるため、後半技が前半の早い相対秒として記録される。全録画を 1 本に畳み込むと後半技が前半
/// タイムラインに紛れる。<see cref="RecordingBranchAnalyzer"/> が開幕 cast でファイルを自動グループ化
/// できるので、グループ別に集計してプルを物理的に分離する。
/// </remarks>
public sealed record SegmentedAggregate(
    AggregatedEvents Combined,
    IReadOnlyList<RecordingSegment> Segments,
    AggregatedEvents CommonAgg);

/// <summary>
/// 1 つのプルセグメント。開幕 cast で識別されるファイル群の集計。
/// </summary>
/// <param name="FirstCastId">このセグメントの判定キャスト ID（"0x28D1" 表記）。</param>
/// <param name="Events">このセグメントのファイル群だけを集計した結果。</param>
/// <param name="CastIds">このセグメント内 cast_start の <see cref="EventKey.Id"/> 集合（実戦アンカ照合用）。</param>
public sealed record RecordingSegment(
    string FirstCastId,
    AggregatedEvents Events,
    IReadOnlySet<string> CastIds);

/// <summary>
/// <see cref="RecordingBranchAnalyzer"/> の分岐検出結果から <see cref="SegmentedAggregate"/> を組み立てる
/// 純粋関数。Dalamud 非依存でユニットテスト可能。
/// </summary>
public static class RecordingSegmentBuilder
{
    private const string CastStartType = "cast_start";

    /// <summary>
    /// 分岐検出結果と全合算集計から、セグメント分離済み集計を構築する。
    /// グループが 2 未満（分岐なし / データ不足 / 解析失敗で null）のときは従来どおり
    /// <paramref name="combined"/> をそのまま使うフォールバックを返す（他コンテンツの挙動を変えない）。
    /// </summary>
    /// <remarks>
    /// 限界: <see cref="BranchDetectionResult.OutlierGroups"/>（同一パターンが 1 本しか無い単発プル）の
    /// イベントは、確定セグメントの <see cref="RecordingSegment.Events"/> には含まれない（Groups のみを
    /// セグメント化するため）。同じパターンを 2 本以上録画すれば主要グループに昇格して解消される。
    /// 確定前の <see cref="SegmentedAggregate.CommonAgg"/> やフォールバックの combined には含まれる。
    /// </remarks>
    public static SegmentedAggregate BuildSegmentedAggregate(
        BranchDetectionResult? result,
        AggregatedEvents combined)
    {
        IReadOnlyList<BranchGroup>? groups = result?.Groups;
        if (groups is null || groups.Count < 2)
        {
            return new SegmentedAggregate(combined, Array.Empty<RecordingSegment>(), EmptyLike(combined));
        }

        var segments = new List<RecordingSegment>(groups.Count);
        foreach (var group in groups)
        {
            AggregatedEvents events = group.AggregatedEvents ?? EmptyLike(combined);
            var castIds = new HashSet<string>(
                events.Events
                    .Where(e => e.Key.Type == CastStartType && !string.IsNullOrEmpty(e.Key.Id))
                    .Select(e => e.Key.Id!),
                StringComparer.OrdinalIgnoreCase);
            segments.Add(new RecordingSegment(group.FirstCastId, events, castIds));
        }

        var common = BuildCommonAgg(segments, combined);
        return new SegmentedAggregate(combined, segments, common);
    }

    /// <summary>
    /// 全セグメントに共通して現れる cast_start のみで構成した「確定前表示用」集計。
    /// どのセグメントにも属する技だけなので、前半/後半を取り違えても誤表示にならない。
    /// 共通技が無い（Sigma 等）場合は空＝確定前は安全に非表示。
    /// </summary>
    private static AggregatedEvents BuildCommonAgg(
        IReadOnlyList<RecordingSegment> segments,
        AggregatedEvents template)
    {
        var commonSet = new HashSet<string>(segments[0].CastIds, StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < segments.Count; i++)
        {
            commonSet.IntersectWith(segments[i].CastIds);
        }
        if (commonSet.Count == 0)
        {
            return EmptyLike(template);
        }

        var picked = new List<AggregatedEvent>(commonSet.Count);
        foreach (var id in commonSet)
        {
            AggregatedEvent? best = null;
            foreach (var segment in segments)
            {
                foreach (var ev in segment.Events.Events)
                {
                    if (ev.Key.Type != CastStartType ||
                        !string.Equals(ev.Key.Id, id, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    // 確定前は「最も早く来うる時刻」で見せるのが安全（取りこぼし防止）。
                    if (best is null || ev.FirstSeenSeconds < best.FirstSeenSeconds)
                    {
                        best = ev;
                    }
                }
            }
            if (best is not null)
            {
                picked.Add(best);
            }
        }

        return new AggregatedEvents(picked, template.BattleCount, picked.Count, template.RecordingFileCount);
    }

    private static AggregatedEvents EmptyLike(AggregatedEvents template)
        => new(
            Array.Empty<AggregatedEvent>(),
            template?.BattleCount ?? 0,
            0,
            template?.RecordingFileCount ?? 0);
}
