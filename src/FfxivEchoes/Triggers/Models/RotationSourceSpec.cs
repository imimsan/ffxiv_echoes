using System.Text.Json.Serialization;

namespace FfxivEchoes.Triggers.Models;

/// <summary>
/// AoE の向き（rotation）をどこから取るかを宣言する種別。
/// Splatoon の <c>UseCastRotation</c> / <c>RotationOverride</c> / <c>LimitRotation</c> 系の
/// 機能に相当。
/// </summary>
public enum RotationKind
{
    /// <summary>
    /// actor の現在の rotation を毎フレーム参照（actor が回ると AoE も回る）。
    /// </summary>
    Live,

    /// <summary>
    /// cast 開始時の rotation をスナップショットして固定。
    /// FFXIV ボスキャストの大半はキャスト開始で向きが lock されるため、
    /// 通常はこれを使うのが安全。
    /// </summary>
    CastSnapshot,

    /// <summary>
    /// 静的な角度を <see cref="RotationSourceSpec.OverrideDeg"/> で上書き。
    /// </summary>
    Override,

    /// <summary>
    /// 北向き固定（0°）。PT 全員が同方向を向く前提の安置コール用。
    /// </summary>
    NorthAligned,

    /// <summary>
    /// actor の現在ターゲット方向を強制（タンク方向クリーブなど）。
    /// </summary>
    TowardsTarget,
}

/// <summary>
/// rotation の取得方法を指定する宣言的スペック。
/// </summary>
public sealed class RotationSourceSpec
{
    [JsonPropertyName("kind")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RotationKind Kind { get; set; } = RotationKind.CastSnapshot;

    /// <summary>
    /// <see cref="RotationKind.Override"/> 時に使う角度（度数法、0=東、90=南、180=西、-90=北）。
    /// </summary>
    [JsonPropertyName("override_deg")]
    public double? OverrideDeg { get; set; }
}
