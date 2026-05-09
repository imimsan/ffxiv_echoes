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
public sealed class MinimapWindow : Window, IDisposable
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
        int? aoeCastType = null)
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
            AoeCastType: aoeCastType,
            ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(ttl));
        lock (_gate)
        {
            _items.Add(item);
        }
        IsOpen = true;
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

        // すべての active items を Priority desc, ExpiresAt asc でソート
        ArenaItem[] activeSorted;
        lock (_gate)
        {
            _items.RemoveAll(it => it.ExpiresAt <= now);
            activeSorted = _items
                .OrderByDescending(it => it.Priority)
                .ThenBy(it => it.ExpiresAt)
                .Take(3)  // 最大 3 件まで同時表示（古い・低優先度は省略）
                .ToArray();
        }

        if (activeSorted.Length == 0)
        {
            IsOpen = false;
            return;
        }

        var draw = ImGui.GetWindowDrawList();
        var scale = ImGuiHelpers.GlobalScale;

        // 1 枚目: フルサイズ。2-3 枚目: 半分サイズで並べる
        for (var idx = 0; idx < activeSorted.Length; idx++)
        {
            var item = activeSorted[idx];
            var tilePos = ImGui.GetCursorScreenPos();
            // 最初は full、それ以降は 60%
            var tileScale = idx == 0 ? 1.0f : 0.6f;
            var size = ArenaSize * scale * tileScale;
            var center = new Vector2(tilePos.X + size * 0.5f, tilePos.Y + size * 0.5f);
            var r = size * 0.5f - 4f * scale;

            DrawArena(draw, center, r);
            DrawGimmickBody(draw, center, r, item);
            // 実 AoE 形状の幾何学的描画（Lumina の CastType + 半径から正確な形を描く）
            DrawActualAoeShape(draw, center, r, item);
            DrawSafeZoneOverlay(draw, center, r, scale * tileScale, item);
            DrawStrategyPositions(draw, center, r, scale * tileScale, item);
            DrawBoss(draw, center, r, scale * tileScale, item);
            DrawPlayerPositions(draw, center, r, scale * tileScale, item);

            ImGui.Dummy(new Vector2(size, size));

            var remaining = (item.ExpiresAt - now).TotalSeconds;
            var calloutText = idx == 0
                ? item.Callout
                : $"次→ {item.Callout}";
            var subText = $"{Math.Max(0, remaining):0.0}s";
            DrawCallout(draw, tilePos, size, scale * tileScale, calloutText, subText);
            ImGui.Dummy(new Vector2(size, CalloutHeight * scale * tileScale));
            ImGui.Spacing();
        }
    }

    // ── 描画ヘルパ ─────────────────────────────────────────────────

    private static void DrawArena(ImDrawListPtr draw, Vector2 center, float r)
    {
        draw.AddCircleFilled(center, r, ColBg, 64);
        draw.AddCircle(center, r, ColBorder, 64, 1.5f);
    }

    private void DrawGimmickBody(ImDrawListPtr draw, Vector2 center, float r, ArenaItem item)
    {
        switch (item.Gimmick)
        {
            case "outer_ring":
                // 外周が危険、中央安置
                draw.AddCircleFilled(center, r * 0.96f, ColDanger, 64);
                draw.AddCircleFilled(center, r * 0.45f, ColSafe, 48);
                DrawDashedCircle(draw, center, r * 0.45f, ColSafeLine, 2.5f, 24);
                AddCenteredText(draw, center + new Vector2(0, r * 0.62f), "SAFE", ColSafeLine, 1.05f);
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

            default:
                AddCenteredText(draw, center, $"unknown: {item.Gimmick}", ColText, 0.9f);
                break;
        }
    }

    /// <summary>
    /// Lumina の CastType + AoE 半径から AoE の実形状をミニマップに幾何学的に描画。
    /// 抽象 gimmick（inner_circle 等）と独立に、実際のテレグラフ形状で「これが危険」と示す。
    /// CastType: 2=ターゲット中心円、3=コーン、4=直線、5=PB AoE、6=Donut。
    /// </summary>
    private void DrawActualAoeShape(ImDrawListPtr draw, Vector2 mapCenter, float mapR, ArenaItem item)
    {
        if (item.AoeRadius is not { } radiusM || radiusM <= 0) return;
        if (item.AoeCastType is not { } castType) return;

        var origin = item.SourceWorld is { } sw &&
                     TryProjectWorldToMap(mapCenter, mapR, item, sw, out var sp)
            ? sp
            : mapCenter;

        var pixelRadius = ArenaProjection.WorldRadiusToMap(radiusM, item.ArenaRadius, mapR);
        if (pixelRadius < 4f) pixelRadius = 4f;

        // 半透明赤で塗り、外周線で形を強調
        var fill = (ColDanger & 0x00FFFFFFu) | 0x55000000u;
        var stroke = (ColDangerLine & 0x00FFFFFFu) | 0xFF000000u;

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
                var innerR = pixelRadius * 0.35f;
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
            case 3: // Cone
            case 4: // Line（細いコーンとして描画）
            {
                var facing = item.DirectionAngleRad ?? 0f;
                var halfFan = castType == 4
                    ? MathF.PI / 12f      // ~30° 全角の細い扇形
                    : (item.FanDeg > 0 ? (float)(item.FanDeg * Math.PI / 360.0) : MathF.PI / 4f);

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
            case 7:  // Cross / 十字（簡易：ソース位置に十字線）
            case 8:  // Multi-cell (rare)
            {
                var cross = pixelRadius * 0.7f;
                draw.AddLine(new Vector2(origin.X - cross, origin.Y),
                              new Vector2(origin.X + cross, origin.Y), stroke, 4f);
                draw.AddLine(new Vector2(origin.X, origin.Y - cross),
                              new Vector2(origin.X, origin.Y + cross), stroke, 4f);
                draw.AddCircleFilled(origin, pixelRadius * 0.15f, fill, 16);
                break;
            }
            case 10:  // 別形 Donut
            case 11:  // (rare)
            case 12:  // (rare)
            case 13:  // (rare)
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

        SafeZoneContext snapshot;
        try
        {
            snapshot = _contextBuilder.Build();
        }
        catch
        {
            return;
        }

        var arenaCenter = snapshot.ArenaCenter;
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
            var snapshot = _contextBuilder.Build();
            mapPos = ArenaProjection.ProjectWorldToMap(
                mapCenter,
                mapR,
                snapshot.ArenaCenter,
                item.ArenaRadius,
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

        foreach (var position in item.StrategyPositions)
        {
            var nx = (float)(position.X / item.ArenaRadius);
            var nz = (float)(position.Z / item.ArenaRadius);
            var dist = MathF.Sqrt(nx * nx + nz * nz);
            if (dist > 1.0f)
            {
                var clamp = 0.97f / dist;
                nx *= clamp;
                nz *= clamp;
            }

            var point = new Vector2(mapCenter.X + nx * mapR, mapCenter.Y + nz * mapR);
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
    private void DrawPlayerPositions(ImDrawListPtr draw, Vector2 center, float r, float scale, ArenaItem item)
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

        var arenaCenter = snapshot.ArenaCenter;
        var radius = item.ArenaRadius;
        if (radius <= 0)
        {
            return;
        }

        var selfPos = snapshot.SelfPosition;
        var bossIds = new HashSet<ulong>();
        foreach (var bossNpc in snapshot.Bosses)
        {
            bossIds.Add(bossNpc.GameObjectId);
        }

        var primaryBossId = snapshot.Boss?.GameObjectId ?? 0UL;
        foreach (var bossNpc in snapshot.Bosses)
        {
            if (bossNpc.GameObjectId == primaryBossId)
            {
                continue;
            }

            var bossPos = new Vector3(bossNpc.Position.X, bossNpc.Position.Y, bossNpc.Position.Z);
            DrawPositionDot(draw, center, r, arenaCenter, radius, bossPos,
                ColBoss, ColText, 5.5f * scale, ringThickness: 1.6f);
        }

        if (item.SourceWorld is { } sourceWorld)
        {
            DrawPositionDot(draw, center, r, arenaCenter, radius, sourceWorld,
                ColScatterLine, ColText, 7.0f * scale, ringThickness: 2.0f);
        }

        // 他の敵 NPC（ボス以外、HP > 0）を赤いドットで描画
        try
        {
            foreach (var obj in _objectTable)
            {
                if (obj is not IBattleNpc npc) continue;
                if (bossIds.Contains(npc.GameObjectId)) continue;
                if (npc.MaxHp == 0) continue;
                if (!IsEnemy(npc)) continue;

                var pos = new Vector3(npc.Position.X, npc.Position.Y, npc.Position.Z);
                // アリーナ範囲外（半径 1.5 倍より遠く）はスキップ：他のフィールド敵を拾わない
                var dx = pos.X - arenaCenter.X;
                var dz = pos.Z - arenaCenter.Z;
                if (dx * dx + dz * dz > radius * radius * 2.25f) continue;

                DrawPositionDot(draw, center, r, arenaCenter, radius, pos,
                    ColEnemy, ColEnemyRing, 4f * scale);
            }
        }
        catch { /* ObjectTable 走査中の例外は無視 */ }

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

            DrawPositionDot(draw, center, r, arenaCenter, radius, memberPos,
                fillColor, ColPartyMemberRing, 4f * scale);
        }

        // 自分は明緑 + 太い白リングで前景強調
        if (selfPos != Vector3.Zero)
        {
            DrawPositionDot(draw, center, r, arenaCenter, radius, selfPos,
                ColPlayerSelf, ColPlayerSelfRing, 5.5f * scale, ringThickness: 1.8f);
        }
    }

    /// <summary>
    /// 世界座標 (X / Z 平面) をミニマップ円内の画素座標に変換してドットを描画。
    /// アリーナ円の外に出る場合は外周ぎりぎりに丸める。
    /// </summary>
    private static void DrawPositionDot(
        ImDrawListPtr draw, Vector2 mapCenter, float mapR,
        Vector3 arenaCenter, float arenaRadius,
        Vector3 worldPos, uint fillColor, uint ringColor, float dotR,
        float ringThickness = 1.2f)
    {
        // FFXIV 座標：X = 東+、Z = 南+。ミニマップは北上、X 右、Y 下なので
        // mapX = cx + dx / arenaR * mapR
        // mapY = cy + dz / arenaR * mapR
        var dx = worldPos.X - arenaCenter.X;
        var dz = worldPos.Z - arenaCenter.Z;
        var nx = dx / arenaRadius;
        var nz = dz / arenaRadius;
        var dist = MathF.Sqrt(nx * nx + nz * nz);
        // 円の外に出るなら外周ぎりぎりに丸める（外側に居ることが分かるよう少し控えめ）
        if (dist > 1.0f)
        {
            var clamp = 0.97f / dist;
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
        int? AoeCastType,
        DateTimeOffset ExpiresAt);
}
