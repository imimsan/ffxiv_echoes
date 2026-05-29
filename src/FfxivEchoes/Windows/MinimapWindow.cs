using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FfxivEchoes.SafeZone;
using FfxivEchoes.Triggers;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Windows;

/// <summary>
/// 俯瞰アリーナ図（ミニマップ）。ギミック発生時に安置/危険ゾーンを上面図で表示する。
/// </summary>
/// <remarks>
/// デモ（demo/demo.js の renderArena）の SVG 描画を ImGui DrawList で再実装したもの。
/// gimmick タイプ：outer_ring / inner_circle / scatter / stack / cone。
/// アクションハンドラ（ArenaViewHandler）から AddArenaView で項目を追加し、
/// duration が経過したら自動で消える。何も無くなったらウィンドウを閉じる。
/// </remarks>
public sealed class MinimapWindow : Window, IDisposable, IMinimapSink
{
    private const float ArenaSize = 200f;
    private const float CalloutHeight = 44f;
    private const float Margin = 8f;

    // 色（RGBA32 little-endian の AABBGGRR 形式 = ImGui の慣習）
    private const uint ColBg = 0xD8141A22;          // arena 背景
    private const uint ColBorder = 0x40FFFFFF;       // arena 外周
    private const uint ColDanger = 0x6B7171F8;       // 危険（赤）
    private const uint ColDangerLine = 0xFF7171F8;
    private const uint ColSafe = 0x8077C534;         // 安置（緑）
    private const uint ColSafeLine = 0xFF7BD391;
    private const uint ColScatter = 0x6BFAA560;      // 散開ポジ（青）
    private const uint ColScatterLine = 0xFFFAA560;
    private const uint ColBoss = 0xFF7C92FB;         // ボス（オレンジ＝ABGR）
    private const uint ColBossDanger = 0xFF7171F8;   // ボス危険状態（赤）
    private const uint ColText = 0xFFFFFFFF;
    private const uint ColCallout = 0xFF24BFFB;      // callout 黄色（amber, ABGR）
    private const uint ColCalloutShadow = 0xFF000000;
    private const uint ColPlayerSelf = 0xFF80FF80;   // 自分（明緑）
    private const uint ColPlayerSelfRing = 0xFFE0FFFF;  // 自分の白リング（強調）
    private const uint ColPartyMember = 0xFFCCCCCC;  // PT 既定（薄灰）
    private const uint ColPartyMemberRing = 0xFFFFFFFF;
    private const uint ColEnemy = 0xFF6B6BF6;        // 他の敵（赤）
    private const uint ColEnemyRing = 0xFFCCCCFF;
    // ロール別色（ABGR 形式）— Lumina ClassJob.Role: 1=Tank, 2=MeleeDPS, 3=Ranged/Caster, 4=Healer
    private const uint ColRoleTank = 0xFFF66B3B;     // 青 (#3B6BF6)
    private const uint ColRoleHealer = 0xFF6BD377;   // 緑 (#77D36B)
    private const uint ColRoleDps = 0xFF6B6BF6;      // 赤 (#F66B6B)
    private const uint ColRoleNonCombat = 0xFFCCCCCC; // 灰

    private readonly List<ArenaItem> _items = new();
    private readonly object _gate = new();
    private readonly SafeZoneContextBuilder _contextBuilder;
    private readonly IObjectTable _objectTable;

    public MinimapWindow(SafeZoneContextBuilder contextBuilder, IObjectTable objectTable)
        : base("##ffxiv-echoes-minimap",
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoBringToFrontOnFocus |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoScrollbar)
    {
        _contextBuilder = contextBuilder;
        _objectTable = objectTable;

        var scale = ImGuiHelpers.GlobalScale;
        Size = new Vector2(ArenaSize + Margin * 2, ArenaSize + CalloutHeight + Margin * 2) * scale;
        SizeCondition = ImGuiCond.FirstUseEver;
        IsOpen = false;
        RespectCloseHotkey = false;
    }

    public void Dispose() { }

    /// <summary>
    /// ギミックを 1 件追加。duration 秒経過すると自動で消える。
    /// </summary>
    /// <param name="arenaRadius">アリーナ半径（メートル）。プレイヤー位置プロット用。
    /// 0 や指定なしなら 20m を仮定。</param>
    /// <param name="safeZoneWorld">F4-F7 で計算済みの安置の世界座標（任意）。
    /// 指定すると緑の点線円としてミニマップに重畳描画する。</param>
    /// <param name="safeZoneRadius">安置マーカーの半径（メートル、デフォルト 3m）。</param>
    public void AddArenaView(
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
        uint? autoLuminaCastId = null)
    {
        if (string.IsNullOrEmpty(gimmick))
        {
            return;
        }
        var ttl = durationSec <= 0 ? 5.0 : durationSec;
        var radius = arenaRadius is { } r && r > 0 ? (float)r : 20f;
        var item = new ArenaItem(
            Gimmick: gimmick.ToLowerInvariant(),
            Callout: callout ?? string.Empty,
            Priority: ArenaViewPriority.GetDisplayPriority(gimmick),
            Direction: direction,
            FanDeg: fanDeg ?? 90.0,
            ArenaRadius: radius,
            SafeZoneWorld: safeZoneWorld,
            SafeZoneRadius: safeZoneRadius ?? 3f,
            DirectionAngleRad: directionAngleRad,
            SourceWorld: sourceWorld,
            StrategyPositions: strategyPositions?.ToArray() ?? Array.Empty<StrategyPosition>(),
            AoeRadius: aoeRadius,
            AoeHalfWidthM: aoeHalfWidthM,
            AoeCastType: aoeCastType,
            AoeOmenId: aoeOmenId,
            MultiSourceWorlds: multiSourceWorlds?.ToArray() ?? Array.Empty<Vector3>(),
            ObjectMarkers: objectMarkers?.ToArray() ?? Array.Empty<StrategyObjectMarker>(),
            AoeZones: aoeZones?.ToArray() ?? Array.Empty<StrategyAoeZone>(),
            PartyStatusHighlights: partyStatusHighlights?.ToArray() ?? Array.Empty<StatusHighlightSpec>(),
            ArenaShape: string.IsNullOrEmpty(arenaShape) ? "circle" : arenaShape!.ToLowerInvariant(),
            ArenaHalfWidth: arenaWidth is { } w && w > 0 ? (float)w * 0.5f : radius,
            ArenaHalfDepth: arenaDepth is { } d && d > 0 ? (float)d * 0.5f : radius,
            // アイテム表示中はこの中心を固定して使う。null の場合は描画時に
            // _contextBuilder.Build() から動的に取り、その後ロックする。
            LockedArenaCenter: lockedArenaCenter,
            ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(ttl),
            AutoLuminaCastId: autoLuminaCastId);
        lock (_gate)
        {
            _items.Add(item);
        }
        IsOpen = true;
    }

    /// <summary>
    /// 指定の cast id で登録された Lumina 自動経路の <see cref="ArenaItem"/> を全削除する。
    /// ユーザー定義 zone（<see cref="StrategyAoeZone.SuppressAutoAoe"/> = true）が
    /// 同じ cast に紐付いて入ってきたとき、自動の不正確 AoE をミニマップから消す用途。
    /// </summary>
    public void SuppressAutoLuminaForCast(uint castId)
    {
        if (castId == 0) return;
        lock (_gate)
        {
            _items.RemoveAll(it => it.AutoLuminaCastId == castId);
        }
    }

    /// <summary>
    /// 複数の世界座標すべてに同じ AoE 円を描画する専用 API。
    /// add NPC（ステュクスのケラノウス・エイドロン等）が同時出現したときに
    /// 各 NPC の位置に AoE 範囲を即座にプロットする用途。
    /// </summary>
    public void AddMultiAoeView(
        string callout,
        IReadOnlyList<Vector3> positions,
        float aoeRadiusM,
        double durationSec,
        double? arenaRadius = null,
        int aoeCastType = 2,
        string? arenaShape = null,
        double? arenaWidth = null,
        double? arenaDepth = null,
        Vector3? lockedArenaCenter = null)
    {
        if (positions is null || positions.Count == 0)
        {
            return;
        }
        AddArenaView(
            gimmick: "multi_aoe",
            callout: callout,
            durationSec: durationSec,
            direction: null,
            fanDeg: null,
            arenaRadius: arenaRadius,
            aoeRadius: aoeRadiusM,
            aoeCastType: aoeCastType,
            multiSourceWorlds: positions,
            arenaShape: arenaShape,
            arenaWidth: arenaWidth,
            arenaDepth: arenaDepth,
            lockedArenaCenter: lockedArenaCenter);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
        }
        IsOpen = false;
    }

    public override void Draw()
    {
        var now = DateTimeOffset.UtcNow;

        // すべての active items を Priority desc, ExpiresAt asc でソート。
        // Lumina 自動 AoE が同時に複数ある場合は、別タイルにせず 1 枚の地図へ重ねる。
        ArenaDisplayGroup[] activeGroups;
        lock (_gate)
        {
            _items.RemoveAll(it => it.ExpiresAt <= now);
            var activeSorted = _items
                .OrderByDescending(it => it.Priority)
                .ThenBy(it => it.ExpiresAt)
                .ToArray();
            activeGroups = BuildDisplayGroups(activeSorted)
                .Take(3)  // 最大 3 グループまで同時表示（古い・低優先度は省略）
                .ToArray();
        }

        if (activeGroups.Length == 0)
        {
            IsOpen = false;
            return;
        }

        var draw = ImGui.GetWindowDrawList();
        var scale = ImGuiHelpers.GlobalScale;

        // 1 枚目: フルサイズ。2-3 枚目: 半分サイズで並べる
        for (var idx = 0; idx < activeGroups.Length; idx++)
        {
            var group = activeGroups[idx];
            var item = group.Primary;
            var tilePos = ImGui.GetCursorScreenPos();
            // 最初は full、それ以降は 60%
            var tileScale = idx == 0 ? 1.0f : 0.6f;
            var size = ArenaSize * scale * tileScale;
            var center = new Vector2(tilePos.X + size * 0.5f, tilePos.Y + size * 0.5f);
            var r = size * 0.5f - 4f * scale;

            DrawArena(draw, center, r, item);
            foreach (var layer in group.Items)
            {
                DrawGimmickBody(draw, center, r, layer);
                // 実 AoE 形状の幾何学的描画（Lumina の CastType + 半径から正確な形を描く）
                DrawActualAoeShape(draw, center, r, layer);
            }
            DrawSafeZoneOverlay(draw, center, r, scale * tileScale, item);
            DrawStrategyPositions(draw, center, r, scale * tileScale, item);
            var liveLayerMode = GetLiveLayerMode(item);
            if (liveLayerMode == MinimapLiveLayerMode.Full)
            {
                DrawBoss(draw, center, r, scale * tileScale, item);
            }
            if (liveLayerMode != MinimapLiveLayerMode.None)
            {
                DrawPlayerPositions(
                    draw,
                    center,
                    r,
                    scale * tileScale,
                    item,
                    drawLiveContext: liveLayerMode == MinimapLiveLayerMode.Full);
            }

            ImGui.Dummy(new Vector2(size, size));

            var remaining = group.Items.Max(it => (it.ExpiresAt - now).TotalSeconds);
            var callout = group.Callout;
            var calloutText = idx == 0
                ? callout
                : $"次→ {callout}";
            var subText = $"{Math.Max(0, remaining):0.0}s";
            DrawCallout(draw, tilePos, size, scale * tileScale, calloutText, subText);
            ImGui.Dummy(new Vector2(size, CalloutHeight * scale * tileScale));
            ImGui.Spacing();
        }
    }

    private static IReadOnlyList<ArenaDisplayGroup> BuildDisplayGroups(IReadOnlyList<ArenaItem> sortedItems)
    {
        var layerableAuto = sortedItems.Where(IsLayerableAutoAoe).ToArray();
        var shouldLayerAuto = layerableAuto.Length > 1;
        var emittedAutoLayer = false;
        var groups = new List<ArenaDisplayGroup>();

        foreach (var item in sortedItems)
        {
            if (shouldLayerAuto && IsLayerableAutoAoe(item))
            {
                if (!emittedAutoLayer)
                {
                    groups.Add(new ArenaDisplayGroup(layerableAuto));
                    emittedAutoLayer = true;
                }
                continue;
            }

            groups.Add(new ArenaDisplayGroup(new[] { item }));
        }

        return groups;
    }

    private static bool IsLayerableAutoAoe(ArenaItem item)
    {
        return MinimapDisplayGroupingPolicy.ShouldLayerAutoAoe(
            item.AutoLuminaCastId,
            item.AoeRadius,
            item.AoeCastType,
            item.StrategyPositions.Count,
            item.ObjectMarkers.Count,
            item.AoeZones.Count);
    }

    // ── 描画ヘルパ ─────────────────────────────────────────────────

    private static MinimapLiveLayerMode GetLiveLayerMode(ArenaItem item)
    {
        return MinimapLiveLayerPolicy.GetLiveLayerMode(
            item.Gimmick,
            item.StrategyPositions.Count,
            item.ObjectMarkers.Count,
            item.AoeZones.Count);
    }

    private static void DrawArena(ImDrawListPtr draw, Vector2 center, float r, ArenaItem item)
    {
        // 形状ごとに背景＋境界線を描き分ける。矩形系は ArenaHalfWidth/Depth と
        // ArenaRadius の比率で縦横比を保つ。
        if (string.Equals(item.ArenaShape, "circle", StringComparison.OrdinalIgnoreCase))
        {
            draw.AddCircleFilled(center, r, ColBg, 64);
            draw.AddCircle(center, r, ColBorder, 64, 1.5f);
            return;
        }

        // 矩形：半径 r をアリーナの「最大半径」とみなし、縦横比を反映
        var arenaMax = MathF.Max(item.ArenaHalfWidth, item.ArenaHalfDepth);
        if (arenaMax <= 0) arenaMax = item.ArenaRadius;
        var hw = r * (item.ArenaHalfWidth / arenaMax);
        var hd = r * (item.ArenaHalfDepth / arenaMax);
        var rectMin = new Vector2(center.X - hw, center.Y - hd);
        var rectMax = new Vector2(center.X + hw, center.Y + hd);
        draw.AddRectFilled(rectMin, rectMax, ColBg);
        draw.AddRect(rectMin, rectMax, ColBorder, 0f, ImDrawFlags.None, 1.5f);
    }

    private void DrawGimmickBody(ImDrawListPtr draw, Vector2 center, float r, ArenaItem item)
    {
        // ユーザーが AoE ゾーンを明示的に定義しているなら、そちらを正解として優先描画する。
        // 旧 gimmick タイプ（outer_ring / inner_circle / donut / scatter / stack 等）は
        // 半径 55% / 96% といったハードコード値で描かれるので、実ボス技サイズと乖離しがち。
        // AoeZones がある = ユーザーが「正確な範囲はこっち」と意思表示しているので、
        // 旧 gimmick のハードコード描画は抑制し、二重表示を避ける。
        if (item.AoeZones.Count > 0)
        {
            DrawUserAoeZones(draw, center, r, item);
            DrawUserObjectMarkers(draw, center, r, item);
            return;
        }

        switch (item.Gimmick)
        {
            case "outer_ring":
                // 外周が危険、中央安置。
                // AoeRadius が提供されていれば DrawActualAoeShape が donut を実半径 + 内径 30%
                // で正確に描くため、ここの近似描画は二重描画になる。skip して委譲する。
                // （以前は内径 45% でハードコードされ、DrawActualAoeShape の 30% と乖離していた）
                if (item.AoeRadius is { } aoeOuter && aoeOuter > 0)
                {
                    break;
                }
                draw.AddCircleFilled(center, r * 0.96f, ColDanger, 64);
                draw.AddCircleFilled(center, r * 0.30f, ColSafe, 48);
                DrawDashedCircle(draw, center, r * 0.30f, ColSafeLine, 2.5f, 24);
                AddCenteredText(draw, center + new Vector2(0, r * 0.45f), "SAFE", ColSafeLine, 1.05f);
                break;

            case "inner_circle":
                {
                    // 中央が危険、外周安置。ソース位置に AoE を実半径で描画。
                    var origin = item.SourceWorld is { } sw &&
                                 TryProjectWorldToMap(center, r, item, sw, out var sp)
                        ? sp
                        : center;
                    var aoeR = item.AoeRadius is { } aoeM && aoeM > 0
                        ? ArenaProjection.WorldRadiusToMap(aoeM, item.ArenaRadius, r)
                        : r * 0.55f;
                    if (aoeR < 6f) aoeR = 6f;
                    draw.AddCircleFilled(origin, aoeR, ColDanger, 48);
                    draw.AddCircle(origin, aoeR, ColDangerLine, 48, 2f);
                    AddCenteredText(draw, origin, "!", ColText, 1.4f);
                    AddCenteredText(draw, center + new Vector2(0, r * 0.85f), "外周回避", ColSafeLine, 0.9f);
                    break;
                }

            case "scatter":
                {
                    var positions = new (float dx, float dy, string label)[]
                    {
                        (0, -0.72f, "N"),
                        (0.72f, 0, "E"),
                        (0, 0.72f, "S"),
                        (-0.72f, 0, "W"),
                    };
                    foreach (var p in positions)
                    {
                        var c = new Vector2(center.X + p.dx * r, center.Y + p.dy * r);
                        draw.AddCircleFilled(c, r * 0.15f, ColScatter, 24);
                        draw.AddCircle(c, r * 0.15f, ColScatterLine, 24, 2f);
                        AddCenteredText(draw, c, p.label, ColText, 1.0f);
                    }
                    break;
                }

            case "stack":
                {
                    var origin = item.SourceWorld is { } sw &&
                                 TryProjectWorldToMap(center, r, item, sw, out var sp)
                        ? sp
                        : center;
                    var aoeR = item.AoeRadius is { } aoeM && aoeM > 0
                        ? ArenaProjection.WorldRadiusToMap(aoeM, item.ArenaRadius, r)
                        : r * 0.42f;
                    if (aoeR < 8f) aoeR = 8f;
                    draw.AddCircleFilled(origin, aoeR, ColScatter, 48);
                    DrawDashedCircle(draw, origin, aoeR, ColScatterLine, 2.5f, 20);
                    AddCenteredText(draw, origin + new Vector2(0, aoeR + 8f), "STACK", ColScatterLine, 1.0f);
                    break;
                }

            case "cone":
                {
                    // CastType=4 (Line) は AutoSafeCallPlanner.Create が gimmick="cone" + FanDeg=30
                    // で送ってくるが、これは「細い扇形 ≒ 直線」を意図している。
                    // 正確な矩形は DrawActualAoeShape の case 4 で描く（床塗りと一致）ので、
                    // ここはスキップして二重描画を避ける。
                    if (item.AoeCastType is 4 or 11 or 12 or 13 && item.AoeRadius is { } && item.AoeRadius > 0)
                    {
                        break;
                    }
                    // DirectionAngleRad（ボス向き等の動的計算結果）が来ていればそれを優先
                    var angle = item.DirectionAngleRad is { } rad
                        ? rad
                        : ParseDirectionAngle(item.Direction) * MathF.PI / 180f;
                    var halfFan = (float)(item.FanDeg * Math.PI / 360.0);
                    var segments = 24;
                    var origin = ArenaProjection.ShouldAnchorGimmickToSource(item.Gimmick) &&
                                 item.SourceWorld is { } sourceWorld &&
                                 TryProjectWorldToMap(center, r, item, sourceWorld, out var sourcePoint)
                        ? sourcePoint
                        : center;
                    var range = r * ArenaProjection.ConeRangeScale(item.FanDeg);
                    var path = new List<Vector2> { origin };
                    for (var i = 0; i <= segments; i++)
                    {
                        var t = (float)i / segments;
                        var a = angle - halfFan + (halfFan * 2f) * t;
                        path.Add(new Vector2(origin.X + MathF.Cos(a) * range, origin.Y + MathF.Sin(a) * range));
                    }
                    foreach (var p in path)
                    {
                        draw.PathLineTo(p);
                    }
                    draw.PathFillConvex(ColDanger);
                    // 外周線
                    for (var i = 0; i <= segments; i++)
                    {
                        var t = (float)i / segments;
                        var a = angle - halfFan + (halfFan * 2f) * t;
                        draw.PathLineTo(new Vector2(origin.X + MathF.Cos(a) * range, origin.Y + MathF.Sin(a) * range));
                    }
                    draw.PathStroke(ColDangerLine, ImDrawFlags.None, 1.5f);
                    break;
                }

            case "half_plane":
                {
                    var angle = item.DirectionAngleRad is { } rad
                        ? rad
                        : ParseDirectionAngle(item.Direction) * MathF.PI / 180f;
                    DrawHalfPlane(draw, center, r, angle);
                    break;
                }

            case "two_side_cleave":
                {
                    // 両翼攻撃：ボス向きを基準に左右両方を危険、前後を安置
                    // facingAngle = 前方向。左右は ±90°
                    var facing = item.DirectionAngleRad is { } rad
                        ? rad
                        : ParseDirectionAngle(item.Direction) * MathF.PI / 180f;
                    var origin = item.SourceWorld is { } sw &&
                                 TryProjectWorldToMap(center, r, item, sw, out var sp)
                        ? sp
                        : center;
                    // 左右それぞれ 90 度の扇を半透明赤で塗る
                    var halfFan = MathF.PI / 4f; // 90度扇 ÷ 2
                    var range = r * 1.05f;
                    var segments = 24;
                    for (var sideIdx = 0; sideIdx < 2; sideIdx++)
                    {
                        var sideAngle = facing + (sideIdx == 0 ? MathF.PI / 2f : -MathF.PI / 2f);
                        var path = new List<Vector2> { origin };
                        for (var i = 0; i <= segments; i++)
                        {
                            var t = (float)i / segments;
                            var a = sideAngle - halfFan + (halfFan * 2f) * t;
                            path.Add(new Vector2(origin.X + MathF.Cos(a) * range,
                                                  origin.Y + MathF.Sin(a) * range));
                        }
                        foreach (var p in path) draw.PathLineTo(p);
                        draw.PathFillConvex(ColDanger);
                    }
                    // 前後の安置ラベル
                    var safeFront = new Vector2(origin.X + MathF.Cos(facing) * r * 0.7f,
                                                 origin.Y + MathF.Sin(facing) * r * 0.7f);
                    var safeBack = new Vector2(origin.X - MathF.Cos(facing) * r * 0.7f,
                                                origin.Y - MathF.Sin(facing) * r * 0.7f);
                    AddCenteredText(draw, safeFront, "SAFE", ColSafeLine, 1.0f);
                    AddCenteredText(draw, safeBack, "SAFE", ColSafeLine, 1.0f);
                    break;
                }

            case "attack":
                DrawDashedCircle(draw, center, r * 0.28f, ColScatterLine, 2.5f, 18);
                draw.AddCircleFilled(center, r * 0.12f, ColScatter, 24);
                AddCenteredText(draw, center, "!", ColText, 1.2f);
                break;

            case "multi_aoe":
                DrawMultiAoeBody(draw, center, r, item);
                break;

            case "user_layout":
                // ユーザーが攻略登録タブで描いたレイアウトのみ表示。
                // 既存の自動 gimmick は何も描かず、AoE ゾーンとオブジェクトマーカーで覆う。
                break;

            default:
                AddCenteredText(draw, center, $"unknown: {item.Gimmick}", ColText, 0.9f);
                break;
        }

        // ユーザー定義の AoE ゾーン（user_layout 以外でも重ねて描ける）
        DrawUserAoeZones(draw, center, r, item);
        DrawUserObjectMarkers(draw, center, r, item);
    }

    /// <summary>
    /// ユーザーが攻略登録で描いた AoE ゾーン群（円・ドーナツ・扇・矩形）を描画。
    /// </summary>
    private void DrawUserAoeZones(ImDrawListPtr draw, Vector2 mapCenter, float mapR, ArenaItem item)
    {
        if (item.AoeZones.Count == 0 || item.ArenaRadius <= 0) return;
        var halfX = item.ArenaHalfWidth > 0 ? item.ArenaHalfWidth : item.ArenaRadius;
        var halfZ = item.ArenaHalfDepth > 0 ? item.ArenaHalfDepth : item.ArenaRadius;
        var radiusScale = MathF.Max(halfX, halfZ);
        if (halfX <= 0 || halfZ <= 0 || radiusScale <= 0) return;

        foreach (var zone in item.AoeZones)
        {
            var origin = ArenaProjection.ProjectRelativeToMap(mapCenter, mapR, halfX, halfZ, zone.X, zone.Z);
            var pixelR = (float)(zone.RadiusM / radiusScale) * mapR;
            if (pixelR < 4f) pixelR = 4f;

            uint baseFill, baseStroke;
            if (zone.IsDanger)
            {
                baseFill = (ColDanger & 0x00FFFFFFu) | 0x55000000u;
                baseStroke = (ColDangerLine & 0x00FFFFFFu) | 0xFF000000u;
            }
            else
            {
                baseFill = (ColSafe & 0x00FFFFFFu) | 0x55000000u;
                baseStroke = (ColSafeLine & 0x00FFFFFFu) | 0xFF000000u;
            }
            // ユーザー指定色があれば塗り色を上書き（線色は同色のα無し）
            if (!string.IsNullOrEmpty(zone.Color))
            {
                var custom = ParseColor(zone.Color, baseStroke);
                baseStroke = custom;
                baseFill = (custom & 0x00FFFFFFu) | 0x55000000u;
            }

            switch ((zone.Shape ?? "circle").ToLowerInvariant())
            {
                case "circle":
                    draw.AddCircleFilled(origin, pixelR, baseFill, 48);
                    draw.AddCircle(origin, pixelR, baseStroke, 48, 2f);
                    break;
                case "donut":
                {
                    var inner = (zone.InnerRadiusM ?? zone.RadiusM * 0.5);
                    var innerPx = (float)(inner / radiusScale) * mapR;
                    if (innerPx < 2f) innerPx = 2f;
                    DrawDonutShape(draw, origin, innerPx, pixelR, baseFill, baseStroke);
                    break;
                }
                case "cone":
                {
                    var rotRad = UserZoneRotationRad(zone, fallbackDeg: -90.0);
                    var halfFan = (float)((zone.FanDeg ?? 90.0) * Math.PI / 360.0);
                    const int segments = 24;
                    var path = new List<Vector2> { origin };
                    for (var i = 0; i <= segments; i++)
                    {
                        var t = (float)i / segments;
                        var a = rotRad - halfFan + (halfFan * 2f) * t;
                        path.Add(new Vector2(origin.X + MathF.Cos(a) * pixelR,
                                              origin.Y + MathF.Sin(a) * pixelR));
                    }
                    foreach (var p in path) draw.PathLineTo(p);
                    draw.PathFillConvex(baseFill);
                    for (var i = 0; i <= segments; i++)
                    {
                        var t = (float)i / segments;
                        var a = rotRad - halfFan + (halfFan * 2f) * t;
                        draw.PathLineTo(new Vector2(origin.X + MathF.Cos(a) * pixelR,
                                                     origin.Y + MathF.Sin(a) * pixelR));
                    }
                    draw.PathStroke(baseStroke, ImDrawFlags.None, 1.5f);
                    break;
                }
                case "rect":
                case "line":
                {
                    var rotRad = UserZoneRotationRad(zone, fallbackDeg: 0.0);
                    var lengthPx = pixelR; // RadiusM は前方の長さ
                    var halfWPx = (AoeGeometryPolicy.ResolveLineHalfWidth(zone.HalfWidthM) / radiusScale) * mapR;
                    if (halfWPx < 6f) halfWPx = 6f;
                    DrawForwardRect(draw, origin, rotRad, lengthPx, halfWPx, baseFill, baseStroke);
                    break;
                }
                case "cross":
                {
                    var rotRad = UserZoneRotationRad(zone, fallbackDeg: 0.0);
                    var halfWPx = (AoeGeometryPolicy.ResolveLineHalfWidth(zone.HalfWidthM) / radiusScale) * mapR;
                    if (halfWPx < 6f) halfWPx = 6f;
                    DrawCenteredRect(draw, origin, rotRad, pixelR, halfWPx, baseFill, baseStroke);
                    DrawCenteredRect(draw, origin, rotRad + MathF.PI / 2f, pixelR, halfWPx, baseFill, baseStroke);
                    break;
                }
                case "donut_cone":
                {
                    var rotRad = UserZoneRotationRad(zone, fallbackDeg: -90.0);
                    var inner = (zone.InnerRadiusM ?? zone.RadiusM * 0.5);
                    var innerPx = (float)(inner / radiusScale) * mapR;
                    if (innerPx < 2f) innerPx = 2f;
                    DrawDonutConeShape(draw, origin, innerPx, pixelR, rotRad, (float)(zone.FanDeg ?? 90.0), baseFill, baseStroke);
                    break;
                }
                case "half_plane":
                {
                    var rotRad = UserZoneRotationRad(zone, fallbackDeg: -90.0);
                    DrawHalfPlane(draw, origin, pixelR, rotRad);
                    break;
                }
            }

            if (!string.IsNullOrEmpty(zone.Label))
            {
                AddCenteredText(draw, origin, zone.Label!, ColText, 0.9f);
            }
        }
    }

    private static float UserZoneRotationRad(StrategyAoeZone zone, double fallbackDeg)
        => (float)(((zone.RotationDeg ?? fallbackDeg) + (zone.RotationOffsetDeg ?? 0.0)) * Math.PI / 180.0);

    private static void DrawForwardRect(
        ImDrawListPtr draw,
        Vector2 origin,
        float rotRad,
        float lengthPx,
        float halfWidthPx,
        uint fill,
        uint stroke)
    {
        var fwd = new Vector2(MathF.Cos(rotRad), MathF.Sin(rotRad));
        var perp = new Vector2(-fwd.Y, fwd.X);
        var p1 = origin - perp * halfWidthPx;
        var p2 = origin + perp * halfWidthPx;
        var p3 = p2 + fwd * lengthPx;
        var p4 = p1 + fwd * lengthPx;
        draw.AddQuadFilled(p1, p2, p3, p4, fill);
        draw.AddQuad(p1, p2, p3, p4, stroke, 1.8f);
    }

    private static void DrawCenteredRect(
        ImDrawListPtr draw,
        Vector2 origin,
        float rotRad,
        float halfLengthPx,
        float halfWidthPx,
        uint fill,
        uint stroke)
    {
        var fwd = new Vector2(MathF.Cos(rotRad), MathF.Sin(rotRad));
        var perp = new Vector2(-fwd.Y, fwd.X);
        var p1 = origin - fwd * halfLengthPx - perp * halfWidthPx;
        var p2 = origin + fwd * halfLengthPx - perp * halfWidthPx;
        var p3 = origin + fwd * halfLengthPx + perp * halfWidthPx;
        var p4 = origin - fwd * halfLengthPx + perp * halfWidthPx;
        draw.AddQuadFilled(p1, p2, p3, p4, fill);
        draw.AddQuad(p1, p2, p3, p4, stroke, 1.5f);
    }

    private static void DrawDonutConeShape(
        ImDrawListPtr draw,
        Vector2 origin,
        float innerR,
        float outerR,
        float rotRad,
        float fanDeg,
        uint fill,
        uint stroke)
    {
        var halfFan = fanDeg * MathF.PI / 360f;
        const int Segments = 32;
        for (var i = 0; i < Segments; i++)
        {
            var t1 = (float)i / Segments;
            var t2 = (float)(i + 1) / Segments;
            var a1 = rotRad - halfFan + (halfFan * 2f) * t1;
            var a2 = rotRad - halfFan + (halfFan * 2f) * t2;
            var pOuter1 = new Vector2(origin.X + MathF.Cos(a1) * outerR, origin.Y + MathF.Sin(a1) * outerR);
            var pOuter2 = new Vector2(origin.X + MathF.Cos(a2) * outerR, origin.Y + MathF.Sin(a2) * outerR);
            var pInner1 = new Vector2(origin.X + MathF.Cos(a1) * innerR, origin.Y + MathF.Sin(a1) * innerR);
            var pInner2 = new Vector2(origin.X + MathF.Cos(a2) * innerR, origin.Y + MathF.Sin(a2) * innerR);
            draw.AddQuadFilled(pOuter1, pOuter2, pInner2, pInner1, fill);
        }

        draw.PathLineTo(new Vector2(origin.X + MathF.Cos(rotRad - halfFan) * innerR, origin.Y + MathF.Sin(rotRad - halfFan) * innerR));
        draw.PathLineTo(new Vector2(origin.X + MathF.Cos(rotRad - halfFan) * outerR, origin.Y + MathF.Sin(rotRad - halfFan) * outerR));
        for (var i = 0; i <= Segments; i++)
        {
            var t = (float)i / Segments;
            var a = rotRad - halfFan + (halfFan * 2f) * t;
            draw.PathLineTo(new Vector2(origin.X + MathF.Cos(a) * outerR, origin.Y + MathF.Sin(a) * outerR));
        }
        draw.PathLineTo(new Vector2(origin.X + MathF.Cos(rotRad + halfFan) * innerR, origin.Y + MathF.Sin(rotRad + halfFan) * innerR));
        for (var i = Segments; i >= 0; i--)
        {
            var t = (float)i / Segments;
            var a = rotRad - halfFan + (halfFan * 2f) * t;
            draw.PathLineTo(new Vector2(origin.X + MathF.Cos(a) * innerR, origin.Y + MathF.Sin(a) * innerR));
        }
        draw.PathStroke(stroke, ImDrawFlags.Closed, 1.5f);
    }

    /// <summary>
    /// ユーザー定義のオブジェクトマーカー（ボス位置・add 等）を描画。
    /// 形状は circle / square / triangle / diamond。
    /// </summary>
    private void DrawUserObjectMarkers(ImDrawListPtr draw, Vector2 mapCenter, float mapR, ArenaItem item)
    {
        if (item.ObjectMarkers.Count == 0 || item.ArenaRadius <= 0) return;
        var scale = ImGuiHelpers.GlobalScale;
        var dotR = 8f * scale;
        var arenaCenter = item.LockedArenaCenter;
        var halfX = item.ArenaHalfWidth > 0 ? item.ArenaHalfWidth : item.ArenaRadius;
        var halfZ = item.ArenaHalfDepth > 0 ? item.ArenaHalfDepth : item.ArenaRadius;
        if (halfX <= 0 || halfZ <= 0) return;

        foreach (var mk in item.ObjectMarkers)
        {
            // ウェイマーク連動：A/B/C/D/1-4 が指定されてれば、現在の実マーカー位置を優先
            float worldX = (float)mk.X;
            float worldZ = (float)mk.Z;
            var live = FfxivEchoes.Capture.WaymarkProvider.TryGetPosition(mk.Waymark);
            if (live is { } wp && arenaCenter is { } ac)
            {
                // ウェイマーク位置はアリーナ中心からの相対 (m) に変換
                worldX = wp.X - ac.X;
                worldZ = wp.Z - ac.Z;
            }
            var p = ArenaProjection.ProjectRelativeToMap(mapCenter, mapR, halfX, halfZ, worldX, worldZ);
            var fill = ParseColor(mk.Color, 0xFF6B6BF6u); // 既定：赤系（ABGR）
            var ring = 0xFFFFFFFFu;

            switch ((mk.Shape ?? "circle").ToLowerInvariant())
            {
                case "square":
                    draw.AddRectFilled(p - new Vector2(dotR, dotR), p + new Vector2(dotR, dotR), fill);
                    draw.AddRect(p - new Vector2(dotR, dotR), p + new Vector2(dotR, dotR), ring, 0f, ImDrawFlags.None, 1.5f);
                    break;
                case "triangle":
                    draw.AddTriangleFilled(
                        p + new Vector2(0, -dotR),
                        p + new Vector2(dotR, dotR * 0.8f),
                        p + new Vector2(-dotR, dotR * 0.8f),
                        fill);
                    draw.AddTriangle(
                        p + new Vector2(0, -dotR),
                        p + new Vector2(dotR, dotR * 0.8f),
                        p + new Vector2(-dotR, dotR * 0.8f),
                        ring, 1.5f);
                    break;
                case "diamond":
                    draw.AddQuadFilled(
                        p + new Vector2(0, -dotR),
                        p + new Vector2(dotR, 0),
                        p + new Vector2(0, dotR),
                        p + new Vector2(-dotR, 0),
                        fill);
                    draw.AddQuad(
                        p + new Vector2(0, -dotR),
                        p + new Vector2(dotR, 0),
                        p + new Vector2(0, dotR),
                        p + new Vector2(-dotR, 0),
                        ring, 1.5f);
                    break;
                default:
                    draw.AddCircleFilled(p, dotR, fill, 18);
                    draw.AddCircle(p, dotR, ring, 18, 1.5f);
                    break;
            }

            if (!string.IsNullOrEmpty(mk.Label))
            {
                AddCenteredText(draw, p + new Vector2(0, -dotR - 8f * scale), mk.Label!, ColText, 0.85f);
            }
        }
    }

    /// <summary>
    /// 複数の世界座標すべてに同じ AoE 形状を描画する。
    /// add NPC が同時出現したときに、各 NPC 位置に対して AoE 範囲を表示する用途。
    /// CastType=6/7/10 ならドーナツ、それ以外は円として描く。
    /// </summary>
    private void DrawMultiAoeBody(ImDrawListPtr draw, Vector2 mapCenter, float mapR, ArenaItem item)
    {
        if (item.MultiSourceWorlds.Count == 0) return;
        var radiusM = item.AoeRadius ?? 6f;
        var pixelR = ArenaProjection.WorldRadiusToMap(radiusM, item.ArenaRadius, mapR);
        if (pixelR < 6f) pixelR = 6f;

        var castType = item.AoeCastType ?? 2;
        var isDonut = AoeResolver.IsDonutShape(castType, 0);

        var fill = (ColDanger & 0x00FFFFFFu) | 0x55000000u;
        var stroke = (ColDangerLine & 0x00FFFFFFu) | (0xFFu << 24);

        for (var i = 0; i < item.MultiSourceWorlds.Count; i++)
        {
            var w = item.MultiSourceWorlds[i];
            if (!TryProjectWorldToMap(mapCenter, mapR, item, w, out var p))
            {
                continue;
            }
            if (isDonut)
            {
                var innerR = pixelR * AoeResolver.DonutInnerRatio(0);
                DrawDonutShape(draw, p, innerR, pixelR, fill, stroke);
                AddCenteredText(draw, p, (i + 1).ToString(), ColText, 1.0f);
            }
            else
            {
                draw.AddCircleFilled(p, pixelR, fill, 32);
                draw.AddCircle(p, pixelR, stroke, 32, 2f);
                AddCenteredText(draw, p, (i + 1).ToString(), ColText, 1.0f);
            }
        }
    }

    /// <summary>
    /// Lumina の CastType + AoE 半径から AoE の実形状をミニマップに幾何学的に描画。
    /// 抽象 gimmick（inner_circle 等）と独立に、実際のテレグラフ形状で「これが危険」と示す。
    /// CastType: 2=ターゲット中心円、3/13=コーン、4/12=直線、5=PB AoE、6/7/10=Donut、11=十字。
    /// </summary>
    private void DrawActualAoeShape(ImDrawListPtr draw, Vector2 mapCenter, float mapR, ArenaItem item)
    {
        // multi_aoe は DrawMultiAoeBody で各位置に既に AoE が描かれているのでここではスキップ
        if (item.Gimmick == "multi_aoe") return;
        // half_plane は DrawGimmickBody の専用描画が正。CastType=4 の直線描画を重ねると
        // 半面ではなく細い矩形に見えるため、実形状レイヤーは抑制する。
        if (item.Gimmick == "half_plane") return;
        if (item.AoeRadius is not { } radiusM || radiusM <= 0) return;
        if (item.AoeCastType is not { } castType) return;

        // 全体攻撃判定：半径がアリーナ半径とほぼ同じ（または超える）AoE は
        // 「アリーナ全体が危険」を意味し、ミニマップで形を描いても意味がない
        // （画面が真っ赤になるだけ、または中央に巨大ドーナツが居座る）。
        // callout だけ残して形状描画はスキップする。
        // CastType を問わず適用：円 (2/5)・Donut (6/7/10)・コーン (3/13)・矩形 (4/12) 全部。
        // 月の底のパラデイグマ (0x67BF) のような Donut 形状全体演出 cast がここで止まる。
        // AutoTelegraphService.OnCastStart と ActorTrackedAoeService.OnCastStarted の
        // 入口ガード (d00bf88) と完全一致させ、描画層でも漏れなく止める。
        if (item.ArenaRadius > 0 &&
            radiusM >= item.ArenaRadius * 0.9f)
        {
            return;
        }

        var origin = item.SourceWorld is { } sw &&
                     TryProjectWorldToMap(mapCenter, mapR, item, sw, out var sp)
            ? sp
            : mapCenter;

        var pixelRadius = ArenaProjection.IsDirectionalAoeCastType(castType)
            ? ArenaProjection.WorldDirectionalLengthToMap(radiusM, item.ArenaRadius, mapR)
            : ArenaProjection.WorldRadiusToMap(radiusM, item.ArenaRadius, mapR);
        if (pixelRadius < 4f) pixelRadius = 4f;

        // 半透明赤で塗り、外周線で形を強調
        var fill = (ColDanger & 0x00FFFFFFu) | 0x55000000u;
        var stroke = (ColDangerLine & 0x00FFFFFFu) | 0xFF000000u;

        var omenId = item.AoeOmenId ?? 0;
        var isDonut = AoeResolver.IsDonutShape(castType, omenId);

        if (isDonut)
        {
            var innerR = pixelRadius * AoeResolver.DonutInnerRatio(omenId);
            DrawDonutShape(draw, origin, innerR, pixelRadius, fill, stroke);
            return;
        }

        switch (castType)
        {
            case 2: // Target-centered circle
            case 5: // PB AoE on caster
            {
                var segments = (int)MathF.Min(64, MathF.Max(24, pixelRadius * 0.4f));
                draw.AddCircleFilled(origin, pixelRadius, fill, segments);
                draw.AddCircle(origin, pixelRadius, stroke, segments, 2f);
                break;
            }
            case 6: // Donut（内側安置）
            {
                var innerR = pixelRadius * 0.30f;
                const int segments = 48;
                for (var i = 0; i < segments; i++)
                {
                    var a1 = (float)(i * Math.PI * 2 / segments);
                    var a2 = (float)((i + 1) * Math.PI * 2 / segments);
                    var pOuter1 = new Vector2(origin.X + MathF.Cos(a1) * pixelRadius,
                                              origin.Y + MathF.Sin(a1) * pixelRadius);
                    var pOuter2 = new Vector2(origin.X + MathF.Cos(a2) * pixelRadius,
                                              origin.Y + MathF.Sin(a2) * pixelRadius);
                    var pInner1 = new Vector2(origin.X + MathF.Cos(a1) * innerR,
                                              origin.Y + MathF.Sin(a1) * innerR);
                    var pInner2 = new Vector2(origin.X + MathF.Cos(a2) * innerR,
                                              origin.Y + MathF.Sin(a2) * innerR);
                    draw.AddQuadFilled(pOuter1, pOuter2, pInner2, pInner1, fill);
                }
                draw.AddCircle(origin, pixelRadius, stroke, segments, 1.5f);
                draw.AddCircle(origin, innerR, stroke, segments, 1.5f);
                break;
            }
            case 3:  // Cone
            case 13: // Target-centered cone
            {
                var facing = item.DirectionAngleRad ?? 0f;
                var halfFan = item.FanDeg > 0
                    ? (float)(item.FanDeg * Math.PI / 360.0)
                    : MathF.PI / 4f;

                const int segments = 24;
                var path = new List<Vector2> { origin };
                for (var i = 0; i <= segments; i++)
                {
                    var t = (float)i / segments;
                    var a = facing - halfFan + (halfFan * 2f) * t;
                    path.Add(new Vector2(origin.X + MathF.Cos(a) * pixelRadius,
                                         origin.Y + MathF.Sin(a) * pixelRadius));
                }
                foreach (var p in path) draw.PathLineTo(p);
                draw.PathFillConvex(fill);
                // 外周線も描画
                draw.PathLineTo(origin);
                for (var i = 0; i <= segments; i++)
                {
                    var t = (float)i / segments;
                    var a = facing - halfFan + (halfFan * 2f) * t;
                    draw.PathLineTo(new Vector2(origin.X + MathF.Cos(a) * pixelRadius,
                                                 origin.Y + MathF.Sin(a) * pixelRadius));
                }
                draw.PathLineTo(origin);
                draw.PathStroke(stroke, ImDrawFlags.None, 1.5f);
                break;
            }
            case 4:  // Line（矩形として描画。床塗り (ActorTrackedAoeService) と shape 一致）
            case 12: // Target-centered line
            {
                // 実半幅が判明していればそれを使い、ない場合のみ既定値 (5m) にフォールバック。
                // 床塗り側 (ActorTrackedAoeService.ResolveHalfWidthForShape) と同じ解決規則。
                var facing = item.DirectionAngleRad ?? 0f;
                var halfWidthM = item.AoeHalfWidthM is { } hw && hw > 0
                    ? hw
                    : AoeGeometryPolicy.DefaultLineHalfWidthM;
                var halfWidthPx = ArenaProjection.WorldRadiusToMap(halfWidthM, item.ArenaRadius, mapR);
                if (halfWidthPx < 6f) halfWidthPx = 6f;
                var fX = MathF.Cos(facing);
                var fY = MathF.Sin(facing);
                var pX = -fY;
                var pY = fX;
                var p1 = new Vector2(origin.X + pX * halfWidthPx, origin.Y + pY * halfWidthPx);
                var p2 = new Vector2(origin.X + fX * pixelRadius + pX * halfWidthPx,
                                     origin.Y + fY * pixelRadius + pY * halfWidthPx);
                var p3 = new Vector2(origin.X + fX * pixelRadius - pX * halfWidthPx,
                                     origin.Y + fY * pixelRadius - pY * halfWidthPx);
                var p4 = new Vector2(origin.X - pX * halfWidthPx, origin.Y - pY * halfWidthPx);
                draw.AddQuadFilled(p1, p2, p3, p4, fill);
                draw.AddLine(p1, p2, stroke, 1.5f);
                draw.AddLine(p2, p3, stroke, 1.5f);
                draw.AddLine(p3, p4, stroke, 1.5f);
                draw.AddLine(p4, p1, stroke, 1.5f);
                break;
            }
            case 11: // Cross / 十字（簡易：ソース位置に十字線）
            {
                var cross = pixelRadius * 0.7f;
                draw.AddLine(new Vector2(origin.X - cross, origin.Y),
                              new Vector2(origin.X + cross, origin.Y), stroke, 4f);
                draw.AddLine(new Vector2(origin.X, origin.Y - cross),
                              new Vector2(origin.X, origin.Y + cross), stroke, 4f);
                draw.AddCircleFilled(origin, pixelRadius * 0.15f, fill, 16);
                break;
            }
            case 8:  // Multi-cell (rare)
            {
                // 不明な形：ソース位置に半径分の半透明円を描いて「警戒」だけ示す
                var segments = (int)MathF.Min(48, MathF.Max(20, pixelRadius * 0.4f));
                draw.AddCircleFilled(origin, pixelRadius, fill, segments);
                draw.AddCircle(origin, pixelRadius, stroke, segments, 1.5f);
                AddCenteredText(draw, origin, $"?{castType}", ColText, 0.85f);
                break;
            }
            default:
                // 完全に未知 → ソースに小さい警戒円
                draw.AddCircle(origin, MathF.Max(8f, pixelRadius * 0.5f), stroke, 24, 2f);
                break;
        }
    }

    private void DrawBoss(ImDrawListPtr draw, Vector2 center, float mapR, float scale, ArenaItem item)
    {
        var bossCenter = item.SourceWorld is { } sourceWorld &&
                         TryProjectWorldToMap(center, mapR, item, sourceWorld, out var sourcePoint)
            ? sourcePoint
            : center;
        var bossR = 6f * scale;
        var color = item.Gimmick == "scatter" ? ColBossDanger : ColBoss;
        draw.AddCircleFilled(bossCenter, bossR, color, 16);
        draw.AddCircle(bossCenter, bossR, ColText, 16, 1.5f);
        if (item.DirectionAngleRad is { } angle)
        {
            var nose = new Vector2(
                bossCenter.X + MathF.Cos(angle) * bossR * 1.9f,
                bossCenter.Y + MathF.Sin(angle) * bossR * 1.9f);
            draw.AddLine(bossCenter, nose, ColText, 2.0f);
        }
    }

    /// <summary>
    /// ドーナツ形状を quad で塗り潰す（外径と内径の間が危険、内径より内が安置）。
    /// 内径 / 外径の比は呼び出し側で渡す（Omen ID から決定）。
    /// </summary>
    private static void DrawDonutShape(ImDrawListPtr draw, Vector2 origin,
        float innerR, float outerR, uint fillColor, uint strokeColor)
    {
        const int Segments = 64;
        for (var i = 0; i < Segments; i++)
        {
            var a1 = (float)(i * Math.PI * 2 / Segments);
            var a2 = (float)((i + 1) * Math.PI * 2 / Segments);
            var pOuter1 = new Vector2(origin.X + MathF.Cos(a1) * outerR,
                                      origin.Y + MathF.Sin(a1) * outerR);
            var pOuter2 = new Vector2(origin.X + MathF.Cos(a2) * outerR,
                                      origin.Y + MathF.Sin(a2) * outerR);
            var pInner1 = new Vector2(origin.X + MathF.Cos(a1) * innerR,
                                      origin.Y + MathF.Sin(a1) * innerR);
            var pInner2 = new Vector2(origin.X + MathF.Cos(a2) * innerR,
                                      origin.Y + MathF.Sin(a2) * innerR);
            draw.AddQuadFilled(pOuter1, pOuter2, pInner2, pInner1, fillColor);
        }
        draw.AddCircle(origin, outerR, strokeColor, Segments, 1.5f);
        draw.AddCircle(origin, innerR, strokeColor, Segments, 1.5f);
        // 中央の安置を緑薄塗りで示す
        var safeFill = (ColSafe & 0x00FFFFFFu) | 0x40000000u;
        draw.AddCircleFilled(origin, innerR * 0.95f, safeFill, 32);
    }

    private static void DrawHalfPlane(ImDrawListPtr draw, Vector2 center, float r, float angle)
    {
        const int Segments = 36;
        var dangerRadius = r * 1.02f;

        draw.PathLineTo(center);
        for (var i = 0; i <= Segments; i++)
        {
            var t = (float)i / Segments;
            var a = angle - MathF.PI / 2f + MathF.PI * t;
            draw.PathLineTo(new Vector2(
                center.X + MathF.Cos(a) * dangerRadius,
                center.Y + MathF.Sin(a) * dangerRadius));
        }
        draw.PathFillConvex(ColDanger);

        var tangent = angle + MathF.PI / 2f;
        var aSide = new Vector2(center.X + MathF.Cos(tangent) * r, center.Y + MathF.Sin(tangent) * r);
        var bSide = new Vector2(center.X - MathF.Cos(tangent) * r, center.Y - MathF.Sin(tangent) * r);
        draw.AddLine(aSide, bSide, ColDangerLine, 2.5f);

        var safeCenter = new Vector2(
            center.X - MathF.Cos(angle) * r * 0.58f,
            center.Y - MathF.Sin(angle) * r * 0.58f);
        AddCenteredText(draw, safeCenter, "SAFE", ColSafeLine, 0.95f);
    }

    /// <summary>
    /// F4-F7 で計算済みの安置位置を、明緑の点線円としてミニマップに重畳描画する。
    /// </summary>
    private void DrawSafeZoneOverlay(ImDrawListPtr draw, Vector2 mapCenter, float mapR, float scale, ArenaItem item)
    {
        if (item.SafeZoneWorld is not { } safeWorld) return;

        Vector3 arenaCenter;
        if (item.LockedArenaCenter is { } locked)
        {
            arenaCenter = locked;
        }
        else
        {
            try
            {
                arenaCenter = _contextBuilder.Build().ArenaCenter;
            }
            catch
            {
                return;
            }
        }
        var arenaR = item.ArenaRadius;
        if (arenaR <= 0) return;

        var dx = (safeWorld.X - arenaCenter.X) / arenaR;
        var dz = (safeWorld.Z - arenaCenter.Z) / arenaR;
        var dist = MathF.Sqrt(dx * dx + dz * dz);
        if (dist > 1.0f)
        {
            // 安置がアリーナ外に計算された場合（直線/扇形系プリセット）は外周ぎりぎりに丸める
            var clamp = 0.97f / dist;
            dx *= clamp;
            dz *= clamp;
        }
        var px = mapCenter.X + dx * mapR;
        var py = mapCenter.Y + dz * mapR;

        var safePixR = item.SafeZoneRadius / arenaR * mapR;
        if (safePixR < 6f * scale) safePixR = 6f * scale;

        // 半透明の塗りつぶし + 点線外周
        var fillColor = (ColSafe & 0x00FFFFFF) | 0x40000000;
        draw.AddCircleFilled(new Vector2(px, py), safePixR, fillColor, 32);
        DrawDashedCircle(draw, new Vector2(px, py), safePixR, ColSafeLine, 2f, 24);

        // 中心マーカー（小さい十字）
        var crossR = 4f * scale;
        draw.AddLine(new Vector2(px - crossR, py), new Vector2(px + crossR, py), ColSafeLine, 1.5f);
        draw.AddLine(new Vector2(px, py - crossR), new Vector2(px, py + crossR), ColSafeLine, 1.5f);
    }

    private bool TryProjectWorldToMap(
        Vector2 mapCenter,
        float mapR,
        ArenaItem item,
        Vector3 worldPos,
        out Vector2 mapPos)
    {
        mapPos = mapCenter;
        if (item.ArenaRadius <= 0)
        {
            return false;
        }

        try
        {
            // ロック済中心があればそれを優先（描画中ジッタしない）
            Vector3 ac;
            if (item.LockedArenaCenter is { } locked) ac = locked;
            else ac = _contextBuilder.Build().ArenaCenter;
            // 非正方矩形アリーナでも AoE 原点とプレイヤードットの正規化を一致させるため、
            // ProjectRelativeToMap と同じ halfX/halfZ を渡す（P1-5）。寸法が無ければ正方扱い。
            var halfX = item.ArenaHalfWidth > 0 ? item.ArenaHalfWidth : item.ArenaRadius;
            var halfZ = item.ArenaHalfDepth > 0 ? item.ArenaHalfDepth : item.ArenaRadius;
            mapPos = ArenaProjection.ProjectWorldToMap(
                mapCenter,
                mapR,
                ac,
                halfX,
                halfZ,
                worldPos);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void DrawStrategyPositions(ImDrawListPtr draw, Vector2 mapCenter, float mapR, float scale, ArenaItem item)
    {
        if (item.StrategyPositions.Count == 0 || item.ArenaRadius <= 0)
        {
            return;
        }
        var halfX = item.ArenaHalfWidth > 0 ? item.ArenaHalfWidth : item.ArenaRadius;
        var halfZ = item.ArenaHalfDepth > 0 ? item.ArenaHalfDepth : item.ArenaRadius;
        if (halfX <= 0 || halfZ <= 0)
        {
            return;
        }

        foreach (var position in item.StrategyPositions)
        {
            var point = ArenaProjection.ProjectRelativeToMap(mapCenter, mapR, halfX, halfZ, position.X, position.Z);
            var color = ParseColor(position.Color, ColScatterLine);
            var fill = (color & 0x00FFFFFF) | 0x90000000;
            draw.AddCircleFilled(point, 7f * scale, fill, 18);
            draw.AddCircle(point, 7f * scale, color, 18, 1.8f);
            var label = position.Label ?? position.Slot;
            if (!string.IsNullOrEmpty(label))
            {
                AddCenteredText(draw, point, label, ColText, 0.75f);
            }
        }
    }

    /// <summary>
    /// 自分と PT メンバーの世界座標をミニマップ座標に変換してドットで描画する。
    /// </summary>
    private void DrawPlayerPositions(
        ImDrawListPtr draw,
        Vector2 center,
        float r,
        float scale,
        ArenaItem item,
        bool drawLiveContext)
    {
        SafeZoneContext snapshot;
        try
        {
            snapshot = _contextBuilder.Build();
        }
        catch
        {
            // ObjectTable 走査中に例外が出ても描画は続ける
            return;
        }

        // ジッタ防止：アイテムにロック済中心があればそれを使う。なければ
        // 今回の snapshot から取り、以降は固定（描画関数内の参照のみ）。
        var arenaCenter = item.LockedArenaCenter ?? snapshot.ArenaCenter;
        // 矩形系では X/Z 軸を独立に正規化する（円形は両方同じ値を使う）。
        // ArenaHalfWidth / ArenaHalfDepth は AddArenaView で寸法既定があれば設定される。
        var halfX = item.ArenaHalfWidth > 0 ? item.ArenaHalfWidth : item.ArenaRadius;
        var halfZ = item.ArenaHalfDepth > 0 ? item.ArenaHalfDepth : item.ArenaRadius;
        if (halfX <= 0 || halfZ <= 0)
        {
            return;
        }

        var selfPos = snapshot.SelfPosition;
        if (drawLiveContext)
        {
            var primaryBossId = snapshot.Boss?.GameObjectId ?? 0UL;
            foreach (var bossNpc in snapshot.Bosses)
            {
                if (bossNpc.GameObjectId == primaryBossId)
                {
                    continue;
                }

                var bossPos = new Vector3(bossNpc.Position.X, bossNpc.Position.Y, bossNpc.Position.Z);
                DrawPositionDot(draw, center, r, arenaCenter, halfX, halfZ, bossPos,
                    ColBoss, ColText, 5.5f * scale, ringThickness: 1.6f);
            }

            if (item.SourceWorld is { } sourceWorld)
            {
                DrawPositionDot(draw, center, r, arenaCenter, halfX, halfZ, sourceWorld,
                    ColScatterLine, ColText, 7.0f * scale, ringThickness: 2.0f);
            }

            // フィールドマーカー（A/B/C/D・1-4）を描画。設置済のみ表示。
            DrawWaymarkOverlay(draw, center, r, arenaCenter, halfX, halfZ, scale);

            // PT メンバーはロール別色のドット（自分は後で上書き描画するためスキップ）
            const float SelfMatchEpsilonSq = 0.05f * 0.05f;
            foreach (var member in snapshot.Party)
            {
                if (member is null) continue;
                var memberPos = new Vector3(member.Position.X, member.Position.Y, member.Position.Z);
                var dxSelf = memberPos.X - selfPos.X;
                var dzSelf = memberPos.Z - selfPos.Z;
                if ((dxSelf * dxSelf + dzSelf * dzSelf) < SelfMatchEpsilonSq)
                {
                    continue; // 自分自身は後段で前景描画
                }

                uint fillColor;
                try
                {
                    var role = member.ClassJob.Value.Role;
                    fillColor = role switch
                    {
                        1 => ColRoleTank,
                        2 or 3 => ColRoleDps,
                        4 => ColRoleHealer,
                        _ => ColRoleNonCombat,
                    };
                }
                catch
                {
                    fillColor = ColPartyMember;
                }

                // ステータスハイライト：このメンバーが指定バフ／デバフを持っていれば
                // 色とバッジを上書きする
                string? badge = null;
                if (item.PartyStatusHighlights.Count > 0)
                {
                    var hi = MatchStatusHighlight(member, item.PartyStatusHighlights);
                    if (hi is not null)
                    {
                        if (!string.IsNullOrEmpty(hi.Color))
                        {
                            fillColor = ParseColor(hi.Color, fillColor);
                        }
                        badge = hi.Badge;
                    }
                }

                DrawPositionDot(draw, center, r, arenaCenter, halfX, halfZ, memberPos,
                    fillColor, ColPartyMemberRing, 4f * scale);

                if (!string.IsNullOrEmpty(badge))
                {
                    var nx = (memberPos.X - arenaCenter.X) / halfX;
                    var nz = (memberPos.Z - arenaCenter.Z) / halfZ;
                    var px = center.X + nx * r;
                    var py = center.Y + nz * r;
                    AddCenteredText(draw, new Vector2(px, py - 12f * scale), badge!, ColCallout, 1.0f);
                }
            }
        }

        // 自分は明緑 + 太い白リングで前景強調
        if (selfPos != Vector3.Zero)
        {
            DrawPositionDot(draw, center, r, arenaCenter, halfX, halfZ, selfPos,
                ColPlayerSelf, ColPlayerSelfRing, 5.5f * scale, ringThickness: 1.8f);
        }
    }

    /// <summary>
    /// FFXIV のフィールドマーカー（A/B/C/D・1〜4）をライブミニマップに重ねて描画。
    /// </summary>
    /// <remarks>
    /// 設置されていないマーカーはスキップ。色は実ゲーム準拠
    /// （A/1=赤, B/2=黄, C/3=青, D/4=紫）。アリーナ枠の外に出るマーカーは
    /// クランプして方向だけ示す（DrawPositionDot 内のクランプロジックを利用）。
    /// </remarks>
    private static void DrawWaymarkOverlay(
        ImDrawListPtr draw, Vector2 mapCenter, float mapR,
        Vector3 arenaCenter, float halfX, float halfZ, float scale)
    {
        var letters = new[] { "A", "B", "C", "D", "1", "2", "3", "4" };
        // ImGui の uint 色は ABGR（下位から R→G→B→A）。
        // A/1=#E74C3C 赤, B/2=#F1C40F 黄, C/3=#3498DB 青, D/4=#9B59B6 紫
        var colors = new uint[]
        {
            0xFF3C4CE7u, 0xFF0FC4F1u, 0xFFDB9834u, 0xFFB6599Bu,
            0xFF3C4CE7u, 0xFF0FC4F1u, 0xFFDB9834u, 0xFFB6599Bu,
        };
        var dotR = 6.0f * scale;
        for (var i = 0; i < letters.Length; i++)
        {
            var live = FfxivEchoes.Capture.WaymarkProvider.TryGetPosition(letters[i]);
            if (live is null) continue;

            // arenaCenter からの相対 → ミニマップ画素
            var dx = live.Value.X - arenaCenter.X;
            var dz = live.Value.Z - arenaCenter.Z;
            var nx = halfX > 0 ? dx / halfX : 0f;
            var nz = halfZ > 0 ? dz / halfZ : 0f;
            if (MathF.Abs(nx) > 1f || MathF.Abs(nz) > 1f)
            {
                var maxAbs = MathF.Max(MathF.Abs(nx), MathF.Abs(nz));
                var clamp = 0.97f / maxAbs;
                nx *= clamp;
                nz *= clamp;
            }
            var px = mapCenter.X + nx * mapR;
            var py = mapCenter.Y + nz * mapR;

            // 半透明塗り + 外枠
            var fill = (colors[i] & 0x00FFFFFFu) | 0xC0000000u;
            draw.AddCircleFilled(new Vector2(px, py), dotR, fill, 20);
            draw.AddCircle(new Vector2(px, py), dotR, colors[i], 20, 1.5f);
            // 中央に文字（黒、見やすく小さく）
            AddCenteredText(draw, new Vector2(px, py - 4f * scale), letters[i], 0xFF000000u, 0.85f);
        }
    }

    /// <summary>
    /// 世界座標 (X / Z 平面) をミニマップ画素座標に変換してドットを描画。
    /// X 軸は <paramref name="halfX"/>、Z 軸は <paramref name="halfZ"/> で独立に正規化する。
    /// 円形アリーナは halfX == halfZ == 半径、矩形系は halfWidth / halfDepth を渡す。
    /// </summary>
    private static void DrawPositionDot(
        ImDrawListPtr draw, Vector2 mapCenter, float mapR,
        Vector3 arenaCenter, float halfX, float halfZ,
        Vector3 worldPos, uint fillColor, uint ringColor, float dotR,
        float ringThickness = 1.2f)
    {
        // FFXIV 座標：X = 東+、Z = 南+。ミニマップは北上・X 右・Y 下。
        // 矩形対応のため X/Z 軸独立に正規化：
        //   nx = (worldX - centerX) / halfX
        //   nz = (worldZ - centerZ) / halfZ
        var dx = worldPos.X - arenaCenter.X;
        var dz = worldPos.Z - arenaCenter.Z;
        var nx = halfX > 0 ? dx / halfX : 0f;
        var nz = halfZ > 0 ? dz / halfZ : 0f;
        // 範囲外（|nx|>1 or |nz|>1）はマップ枠ぎりぎりに丸める
        if (MathF.Abs(nx) > 1f || MathF.Abs(nz) > 1f)
        {
            var maxAbs = MathF.Max(MathF.Abs(nx), MathF.Abs(nz));
            var clamp = 0.97f / maxAbs;
            nx *= clamp;
            nz *= clamp;
        }
        var px = mapCenter.X + nx * mapR;
        var py = mapCenter.Y + nz * mapR;
        draw.AddCircleFilled(new Vector2(px, py), dotR, fillColor, 16);
        draw.AddCircle(new Vector2(px, py), dotR, ringColor, 16, ringThickness);
    }

    private static void DrawCallout(ImDrawListPtr draw, Vector2 winPos, float size, float scale, string callout, string sub)
    {
        var top = winPos.Y + size + 4f * scale;
        var bgRect1 = new Vector2(winPos.X, top);
        var bgRect2 = new Vector2(winPos.X + size, top + (CalloutHeight - 4f) * scale);
        draw.AddRectFilled(bgRect1, bgRect2, 0xC8000000, 4f);
        draw.AddRect(bgRect1, bgRect2, 0x30FFFFFF, 4f, ImDrawFlags.None, 1f);

        if (!string.IsNullOrEmpty(callout))
        {
            ImGui.PushFont(ImGui.GetFont());
            var textSize = ImGui.CalcTextSize(callout) * 1.15f;
            var textPos = new Vector2(
                bgRect1.X + (size - textSize.X) * 0.5f,
                bgRect1.Y + 4f * scale);
            // 影
            draw.AddText(textPos + new Vector2(1, 1), ColCalloutShadow, callout);
            draw.AddText(textPos, ColCallout, callout);
            ImGui.PopFont();
        }

        if (!string.IsNullOrEmpty(sub))
        {
            var subSize = ImGui.CalcTextSize(sub);
            var subPos = new Vector2(
                bgRect1.X + (size - subSize.X) * 0.5f,
                bgRect2.Y - subSize.Y - 4f * scale);
            draw.AddText(subPos, 0xFFAAAAAA, sub);
        }
    }

    private static void DrawDashedCircle(ImDrawListPtr draw, Vector2 center, float r, uint color, float thickness, int dashes)
    {
        var step = MathF.PI * 2f / dashes;
        for (var i = 0; i < dashes; i++)
        {
            if ((i & 1) == 1) continue;
            var a1 = i * step;
            var a2 = (i + 1) * step;
            var p1 = new Vector2(center.X + MathF.Cos(a1) * r, center.Y + MathF.Sin(a1) * r);
            var p2 = new Vector2(center.X + MathF.Cos(a2) * r, center.Y + MathF.Sin(a2) * r);
            draw.AddLine(p1, p2, color, thickness);
        }
    }

    private static void AddCenteredText(ImDrawListPtr draw, Vector2 center, string text, uint color, float scale)
    {
        if (string.IsNullOrEmpty(text)) return;
        var size = ImGui.CalcTextSize(text) * scale;
        var pos = new Vector2(center.X - size.X * 0.5f, center.Y - size.Y * 0.5f);
        // 影
        draw.AddText(pos + new Vector2(1, 1), 0xFF000000, text);
        draw.AddText(pos, color, text);
    }

    private static uint ParseColor(string? hex, uint fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return fallback;
        }

        var s = hex.Trim();
        if (s.StartsWith("#", StringComparison.Ordinal))
        {
            s = s[1..];
        }

        if (s.Length != 6 ||
            !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return fallback;
        }

        var r = (rgb >> 16) & 0xFF;
        var g = (rgb >> 8) & 0xFF;
        var b = rgb & 0xFF;
        return 0xFF000000 | (b << 16) | (g << 8) | r;
    }

    /// <summary>
    /// IBattleNpc が敵側かどうかの簡易判定。BattleNpcKind が Enemy か、または
    /// SubKind から判別できない場合は MaxHp > 0 で「敵対 NPC」とみなす。
    /// </summary>
    /// <summary>
    /// 指定 PT メンバーが <paramref name="specs"/> の何れかにマッチするステータスを
    /// 持っていれば、その spec を返す。最初のマッチで打ち切り。
    /// </summary>
    private static StatusHighlightSpec? MatchStatusHighlight(
        Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter member,
        IReadOnlyList<StatusHighlightSpec> specs)
    {
        try
        {
            var statuses = member.StatusList;
            if (statuses is null) return null;

            foreach (var st in statuses)
            {
                if (st is null || st.StatusId == 0) continue;
                foreach (var spec in specs)
                {
                    if (spec.StatusId is { } sid && sid == st.StatusId) return spec;
                    if (!string.IsNullOrEmpty(spec.StatusName))
                    {
                        var nm = st.GameData.ValueNullable?.Name.ExtractText() ?? string.Empty;
                        if (nm.Contains(spec.StatusName, StringComparison.OrdinalIgnoreCase))
                        {
                            return spec;
                        }
                    }
                }
            }
        }
        catch { /* 取得失敗は無視 */ }
        return null;
    }

    private static bool IsEnemy(IBattleNpc npc)
    {
        try
        {
            // 既存 Preset と同じ判定：Pet 以外 = 敵 NPC とみなす
            return npc.BattleNpcKind != Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind.Pet;
        }
        catch
        {
            return npc.MaxHp > 0;
        }
    }

    private static float ParseDirectionAngle(string? direction)
    {
        // SVG 座標系（Y 下向き）に合わせて、N = -90°、E = 0°、S = 90°、W = 180°
        return direction?.ToUpperInvariant() switch
        {
            "N" => -90f,
            "NE" => -45f,
            "E" => 0f,
            "SE" => 45f,
            "S" => 90f,
            "SW" => 135f,
            "W" => 180f,
            "NW" => -135f,
            _ => -90f,
        };
    }

    private readonly record struct ArenaItem(
        string Gimmick,
        string Callout,
        int Priority,
        string? Direction,
        double FanDeg,
        float ArenaRadius,
        Vector3? SafeZoneWorld,
        float SafeZoneRadius,
        float? DirectionAngleRad,
        Vector3? SourceWorld,
        IReadOnlyList<StrategyPosition> StrategyPositions,
        float? AoeRadius,
        float? AoeHalfWidthM,
        int? AoeCastType,
        uint? AoeOmenId,
        IReadOnlyList<Vector3> MultiSourceWorlds,
        IReadOnlyList<StrategyObjectMarker> ObjectMarkers,
        IReadOnlyList<StrategyAoeZone> AoeZones,
        IReadOnlyList<StatusHighlightSpec> PartyStatusHighlights,
        string ArenaShape,
        float ArenaHalfWidth,
        float ArenaHalfDepth,
        Vector3? LockedArenaCenter,
        DateTimeOffset ExpiresAt,
        /// <summary>
        /// AutoTelegraphService 等の Lumina 自動経路で追加された場合の cast id。
        /// <see cref="SuppressAutoLuminaForCast"/> で同 cast の自動エントリを除去するキー。
        /// ユーザー定義 zone は null。
        /// </summary>
        uint? AutoLuminaCastId);

    private readonly record struct ArenaDisplayGroup(IReadOnlyList<ArenaItem> Items)
    {
        public ArenaItem Primary => Items[0];

        public string Callout
        {
            get
            {
                if (Items.Count <= 1)
                {
                    return Primary.Callout;
                }

                return $"{Primary.Callout} ほか{Items.Count - 1}";
            }
        }
    }
}
