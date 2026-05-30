using System;
using System.Collections.Generic;

namespace FfxivEchoes.Events;

/// <summary>
/// 観測されたすべてのゲーム内イベントが実装するマーカーインターフェイス。
/// </summary>
/// <remarks>
/// 戦闘開始からの相対時刻が必要な購読者は <see cref="FfxivEchoes.Capture.CombatClock"/>
/// 経由で算出する。イベント自体は絶対時刻のみを持つ。
/// </remarks>
public interface IGameEvent
{
    DateTimeOffset Timestamp { get; }
}

// ─── 戦闘ライフサイクル ───────────────────────────────────────────────

public sealed record CombatStartedEvent(DateTimeOffset Timestamp) : IGameEvent;

public enum CombatEndReason
{
    Unknown,
    Cleared,
    Wiped,
    Left,
}

public sealed record CombatEndedEvent(
    DateTimeOffset Timestamp,
    CombatEndReason Reason
) : IGameEvent;

// ─── ゾーン ───────────────────────────────────────────────────────────

public sealed record ZoneChangedEvent(
    DateTimeOffset Timestamp,
    uint TerritoryId,
    string ZoneName
) : IGameEvent;

// ─── キャスト ──────────────────────────────────────────────────────────

public sealed record CastStartedEvent(
    DateTimeOffset Timestamp,
    uint SourceId,
    string SourceName,
    uint CastActionId,
    string CastActionName,
    float CastTime,
    uint? TargetId,
    System.Numerics.Vector3? TargetWorld = null
) : IGameEvent;

public sealed record CastCompletedEvent(
    DateTimeOffset Timestamp,
    uint SourceId,
    string SourceName,
    uint CastActionId,
    string CastActionName
) : IGameEvent;

public sealed record CastCanceledEvent(
    DateTimeOffset Timestamp,
    uint SourceId,
    string SourceName,
    uint CastActionId,
    string CastActionName
) : IGameEvent;

public sealed record ActionUsedEvent(
    DateTimeOffset Timestamp,
    uint SourceId,
    string SourceName,
    uint ActionId,
    string ActionName,
    uint? TargetId,
    bool IsAutoAttack,
    bool IsPlayer = false,
    System.Numerics.Vector3? TargetWorld = null
) : IGameEvent;

// ─── ステータス（バフ／デバフ） ──────────────────────────────────────

public sealed record StatusGainedEvent(
    DateTimeOffset Timestamp,
    uint TargetId,
    string TargetName,
    uint StatusId,
    string StatusName,
    float RemainingTime,
    ushort Stacks,
    uint SourceId,
    bool IsPlayer = false
) : IGameEvent;

public sealed record StatusLostEvent(
    DateTimeOffset Timestamp,
    uint TargetId,
    string TargetName,
    uint StatusId,
    string StatusName,
    bool IsPlayer = false
) : IGameEvent;

public sealed record StatusUpdatedEvent(
    DateTimeOffset Timestamp,
    uint TargetId,
    uint StatusId,
    ushort Stacks,
    float RemainingTime,
    bool IsPlayer = false
) : IGameEvent;

// ─── HP 変化 ─────────────────────────────────────────────────────────

public sealed record HpChangedEvent(
    DateTimeOffset Timestamp,
    uint ActorId,
    string ActorName,
    float HpPct,
    uint CurrentHp,
    uint MaxHp,
    bool IsPlayer = false
) : IGameEvent;

// ─── フェーズ遷移 ────────────────────────────────────────────────

/// <summary>
/// 戦闘中にフェーズ境界を越えたときに発行される。sync_point（cast_start）の通過、または
/// hp_pct 閾値跨ぎを検知元とする。<see cref="FfxivEchoes.Triggers.CurrentPhaseTracker"/> が
/// 購読して現在フェーズ序数を前進させ、過去フェーズのギミックをタイムライン / 読み上げから除外する。
/// </summary>
/// <param name="PhaseId">移行先フェーズの識別子（<see cref="FfxivEchoes.Triggers.Models.MechanicStrategy.Phase"/> と対応）。</param>
public sealed record PhaseTransitionedEvent(
    DateTimeOffset Timestamp,
    string PhaseId
) : IGameEvent;

// ─── オブジェクト出現／消失（F9） ──────────────────────────────────

public sealed record ObjectAppearedEvent(
    DateTimeOffset Timestamp,
    uint ObjectId,
    string ObjectName,
    uint DataId,
    System.Numerics.Vector3 Position,
    bool IsPlayer = false,
    uint? EntityId = null) : IGameEvent;

public sealed record ObjectGroupAppearedEvent(
    DateTimeOffset Timestamp,
    string ObjectName,
    uint DataId,
    int Count,
    IReadOnlyList<System.Numerics.Vector3> Positions) : IGameEvent;

public sealed record ObjectDisappearedEvent(
    DateTimeOffset Timestamp,
    uint ObjectId,
    string ObjectName) : IGameEvent;

// ─── プレイヤー位置（アリーナ寸法推定用） ───────────────────────────

/// <summary>
/// 戦闘中に定期サンプルされる自分（LocalPlayer）の位置。
/// </summary>
/// <remarks>
/// アリーナ寸法を録画から推定する用途。プレイヤーは戦闘で必ず端まで動かされるので、
/// LocalPlayer 位置の bbox がアリーナ床面とほぼ一致する。
/// 戦闘中のみ発火し、戦闘終了後は止める（屋外ぶらつきを混ぜない）。
/// </remarks>
public sealed record LocalPlayerPositionEvent(
    DateTimeOffset Timestamp,
    System.Numerics.Vector3 Position) : IGameEvent;

// ─── トリガー発火（M6 で発行） ─────────────────────────────────────

/// <summary>
/// マッチング済みトリガーの発火イベント。M7 のアクションディスパッチャが購読する。
/// </summary>
/// <param name="Zone">発火時のゾーン名</param>
/// <param name="TriggerId">発火したトリガーの ID</param>
/// <param name="TriggerName">トリガーの表示名（未設定なら null）</param>
/// <param name="Actions">発動するアクション一覧（発火時点でのスナップショット）</param>
/// <param name="SourceEvent">マッチ元のイベント（cast_start などのオリジナル）</param>
public sealed record TriggerFiredEvent(
    DateTimeOffset Timestamp,
    string Zone,
    string TriggerId,
    string? TriggerName,
    IReadOnlyList<FfxivEchoes.Triggers.Models.ActionDefinition> Actions,
    IGameEvent SourceEvent
) : IGameEvent;
