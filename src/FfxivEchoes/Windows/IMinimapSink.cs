using System.Collections.Generic;
using System.Numerics;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Windows;

/// <summary>
/// ミニマップへの描画呼出を受け取る抽象 sink。
/// 本番では <see cref="MinimapWindow"/> が実装し、ImGui で描画する。
/// テスト時 / headless replay では <c>TraceRecorder</c> 等の代替実装に差し替えて、
/// 描画決定だけを JSON にキャプチャできるようにする。
/// </summary>
/// <remarks>
/// シグネチャは <see cref="MinimapWindow"/> の public メソッドと一対一対応する。
/// 描画ロジック自体の改修は MinimapWindow に閉じるため、ここでは型を変えない。
/// </remarks>
public interface IMinimapSink
{
    /// <summary>ギミックを 1 件追加。詳細は <see cref="MinimapWindow.AddArenaView"/> 参照。</summary>
    void AddArenaView(
        string gimmick,
        string? callout,
        double durationSec,
        string? direction,
        double? fanDeg,
        double? arenaRadius,
        Vector3? safeZoneWorld = null,
        float? safeZoneRadius = null,
        float? directionAngleRad = null,
        Vector3? sourceWorld = null,
        IReadOnlyList<StrategyPosition>? strategyPositions = null,
        float? aoeRadius = null,
        float? aoeHalfWidthM = null,
        int? aoeCastType = null,
        uint? aoeOmenId = null,
        IReadOnlyList<Vector3>? multiSourceWorlds = null,
        IReadOnlyList<StrategyObjectMarker>? objectMarkers = null,
        IReadOnlyList<StrategyAoeZone>? aoeZones = null,
        IReadOnlyList<StatusHighlightSpec>? partyStatusHighlights = null,
        string? arenaShape = null,
        double? arenaWidth = null,
        double? arenaDepth = null,
        Vector3? lockedArenaCenter = null,
        uint? autoLuminaCastId = null);

    /// <summary>複数 actor に同じ AoE 円を一括描画する専用 API。</summary>
    void AddMultiAoeView(
        string callout,
        IReadOnlyList<Vector3> positions,
        float aoeRadiusM,
        double durationSec,
        double? arenaRadius = null,
        int aoeCastType = 2,
        string? arenaShape = null,
        double? arenaWidth = null,
        double? arenaDepth = null,
        Vector3? lockedArenaCenter = null);

    /// <summary>指定の cast id で登録された Lumina 自動経路の項目を全削除する。</summary>
    void SuppressAutoLuminaForCast(uint castId);

    /// <summary>全アイテムをクリアしてウィンドウを閉じる。</summary>
    void Clear();
}
