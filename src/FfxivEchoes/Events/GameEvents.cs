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
    uint? TargetId
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

// ─── ステータス（バフ／デバフ） ──────────────────────────────────────

public sealed record StatusGainedEvent(
    DateTimeOffset Timestamp,
    uint TargetId,
    string TargetName,
    uint StatusId,
    string StatusName,
    float RemainingTime,
    ushort Stacks,
    uint SourceId
) : IGameEvent;

public sealed record StatusLostEvent(
    DateTimeOffset Timestamp,
    uint TargetId,
    string TargetName,
    uint StatusId,
    string StatusName
) : IGameEvent;

public sealed record StatusUpdatedEvent(
    DateTimeOffset Timestamp,
    uint TargetId,
    uint StatusId,
    ushort Stacks,
    float RemainingTime
) : IGameEvent;

// ─── HP 変化 ─────────────────────────────────────────────────────────

public sealed record HpChangedEvent(
    DateTimeOffset Timestamp,
    uint ActorId,
    string ActorName,
    float HpPct,
    uint CurrentHp,
    uint MaxHp
) : IGameEvent;
