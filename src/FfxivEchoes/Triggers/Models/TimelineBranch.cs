using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FfxivEchoes.Triggers.Models;

/// <summary>
/// タイムライン分岐：戦闘中に観測したイベントによって「どちらの mechanic 列を表示するか」を
/// 切り替えるための定義。例：滅エヌオーで「最初に エアロジャ が来たらパターン1、フレアが来たら
/// パターン2」のような分岐。
/// </summary>
/// <remarks>
/// <para>
/// 戦闘開始から <see cref="BranchCondition.WindowSec"/> 秒以内に <see cref="Condition"/> を
/// 満たすイベントが観測されたら、この branch が <c>Active</c> に遷移し、他の branch は
/// <c>Rejected</c> になる。<see cref="MechanicStrategy.BranchId"/> がこの id を指す mechanic は
/// active 時のみタイムラインに表示される。
/// </para>
/// <para>
/// <see cref="MechanicStrategy.BranchId"/> が null の mechanic は分岐に関係なく常に表示される
/// （パターン横断の共通ギミック）。
/// </para>
/// </remarks>
public sealed class TimelineBranch
{
    /// <summary>分岐の識別子（例："pattern_aeroja"）。<see cref="MechanicStrategy.BranchId"/> から参照される。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>表示名（例："パターン1（エアロジャ系）"）。タイムライン UI の凡例で使う。</summary>
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    /// <summary>
    /// 分岐グループ。未指定なら "default"。
    /// 同じ group_id の中だけで Active/Rejected を決めるため、P1 と P3 など複数回の分岐を共存できる。
    /// </summary>
    [JsonPropertyName("group_id")]
    public string? GroupId { get; set; }

    /// <summary>この分岐が active になる判定条件。</summary>
    [JsonPropertyName("condition")]
    public BranchCondition Condition { get; set; } = new();
}

/// <summary>
/// 分岐判定条件。<see cref="Type"/> に応じて使用フィールドが変わる。
/// </summary>
public sealed class BranchCondition
{
    /// <summary>条件の種別："first_cast" / "status_gain" / "object_appear"。</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "first_cast";

    /// <summary>first_cast 用：最初に観測されるべき cast id（"0xABCD" 形式 or 10 進）。</summary>
    [JsonPropertyName("cast_id")]
    public string? CastId { get; set; }

    /// <summary>cast_id 不明環境向けの名前マッチ補助（部分一致）。</summary>
    [JsonPropertyName("cast_name")]
    public string? CastName { get; set; }

    /// <summary>status_gain 用：観測されるべき status id。</summary>
    [JsonPropertyName("status_id")]
    public uint? StatusId { get; set; }

    /// <summary>object_appear 用：観測されるべきオブジェクト名（部分一致）。</summary>
    [JsonPropertyName("object_name")]
    public string? ObjectName { get; set; }

    /// <summary>
    /// 戦闘開始からこの秒数以内の観測のみ有効（既定 30 秒）。
    /// 短くしすぎるとネット遅延・録画開始ズレで判定キャストを見逃す。長くしすぎると
    /// 偶然観測した別キャストで誤判定する。多くの絶コンテンツでは 10〜30 秒が安全。
    /// </summary>
    [JsonPropertyName("window_sec")]
    public double WindowSec { get; set; } = 30.0;
}

/// <summary>
/// ランタイムの分岐状態。<see cref="FfxivEchoes.Triggers.BranchObserverService"/> が管理する。
/// JSON シリアライズ対象ではない（戦闘ごとに揮発する状態）。
/// </summary>
public enum BranchStatus
{
    /// <summary>未判定（戦闘開始直後 / window_sec 未経過）。</summary>
    Pending,
    /// <summary>判定条件を満たして有効化された。</summary>
    Active,
    /// <summary>他の branch が active になったため棄却された。</summary>
    Rejected,
}

/// <summary>
/// 分岐状態が変化したときに発行されるイベント（<see cref="FfxivEchoes.Events.IGameEvent"/> ではなく
/// プラグイン内部の通知）。<see cref="FfxivEchoes.Windows.UpcomingEventsWindow"/> と
/// <see cref="FfxivEchoes.Triggers.PredictedCastReminderService"/> が購読してキャッシュ無効化する。
/// </summary>
public sealed record BranchResolvedEvent(
    System.DateTimeOffset Timestamp,
    string Zone,
    string ActiveBranchId,
    IReadOnlyList<string> RejectedBranchIds) : FfxivEchoes.Events.IGameEvent;
