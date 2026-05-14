using System.Text.Json.Serialization;

namespace FfxivEchoes.Triggers.Models;

/// <summary>
/// AoE を表示するかどうかの「実行時状態」フィルタ。
/// Splatoon の <c>LimitDistance</c>, <c>LimitRotation</c>, HP 系チェックに相当する補助条件。
/// 全フィールド null のときは常に true（フィルタしない）。
/// </summary>
public sealed class StateFilter
{
    /// <summary>HP 割合の下限（0.0〜1.0）。これ未満なら非表示。</summary>
    [JsonPropertyName("hp_pct_min")]
    public double? HpPctMin { get; set; }

    /// <summary>HP 割合の上限（0.0〜1.0）。これより大なら非表示（「ピンチでだけ表示」用）。</summary>
    [JsonPropertyName("hp_pct_max")]
    public double? HpPctMax { get; set; }

    /// <summary>自分（プレイヤー）と actor の最大距離 m。これより遠ければ非表示。</summary>
    [JsonPropertyName("distance_max_m")]
    public double? DistanceMaxM { get; set; }

    /// <summary>actor が cast 中のときだけ表示（cast 終了 / cancel で消える）。</summary>
    [JsonPropertyName("only_while_casting")]
    public bool? OnlyWhileCasting { get; set; }

    /// <summary>actor 自身に該当 status (buff/debuff) が付いているときのみ表示。</summary>
    [JsonPropertyName("require_status_id")]
    public uint? RequireStatusId { get; set; }

    /// <summary>actor が指定の cast id を撃っているときのみ表示。</summary>
    [JsonPropertyName("require_cast_id")]
    public uint? RequireCastId { get; set; }
}
