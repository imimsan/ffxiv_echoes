using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using FfxivEchoes.Events;

namespace FfxivEchoes.SafeZone;

/// <summary>
/// 安置計算プリセット実行時のスナップショット。
/// </summary>
public sealed record SafeZoneContext(
    Vector3 SelfPosition,
    Vector3 ArenaCenter,
    IBattleNpc? Boss,
    IReadOnlyList<IPlayerCharacter> Party,
    IReadOnlyDictionary<string, Vector3> FieldMarkers,
    IBattleChara? CastActor,
    IGameEvent? LastEvent,
    IReadOnlyList<IBattleNpc> Bosses);
