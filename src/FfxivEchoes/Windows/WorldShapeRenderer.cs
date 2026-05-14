using System;
using System.Buffers;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace FfxivEchoes.Windows;

/// <summary>
/// Splatoon 系の世界座標多角形を ImGui の path API で塗り描画する静的ヘルパ。
/// <see cref="WorldOverlayWindow"/> の TTL 経路と <see cref="FfxivEchoes.Triggers.ActorTrackedAoeService"/>
/// のライブ経路の両方から共用される。
/// </summary>
/// <remarks>
/// 設計方針：
/// <list type="bullet">
/// <item>すべて床面（y 一定）に貼り付く塗りつぶし AoE として描く。Splatoon の床塗りに同調。</item>
/// <item>セグメント数は半径に応じて増やすが、戦闘中の負荷を抑えるため 64 で打ち止め。</item>
/// <item>fill は半透明 (alpha &lt; 0xA0)、stroke は不透明気味（縁線で可読性確保）。色は呼び元が決める。</item>
/// <item>カメラ裏側の点は <see cref="IGameGui.WorldToScreen"/> が false を返すので、
///       PathLineTo を呼ばないことで自然に省く。閉じ忘れに注意。</item>
/// </list>
/// </remarks>
public static class WorldShapeRenderer
{
    private const int DefaultSegments = 40;

    /// <summary>
    /// 半径に応じて推奨セグメント数を計算。大半径ほど多分割にするが、
    /// 戦闘中に複数 AoE が重なるため 1 frame 予算を優先して強めに上限をかける。
    /// クランプ範囲 [24, 64]。
    /// </summary>
    /// <remarks>
    /// 経験則：r=5m → 30 / r=15m → 42 / r=30m → 60 / r=50m → 64。
    /// r=30m+ の大円でも WorldToScreen 呼び出しを増やしすぎない。
    /// </remarks>
    public static int RecommendSegments(float radius)
    {
        var n = (int)(24f + Math.Max(0f, radius) * 1.2f);
        return Math.Clamp(n, 24, 64);
    }

    /// <summary>
    /// 床面に貼り付く塗りつぶし円。
    /// </summary>
    public static void DrawCircle(
        ImDrawListPtr draw, IGameGui gameGui,
        Vector3 center, float radius, uint fillColor, uint strokeColor, int segments = 0)
    {
        if (radius < 0.1f) return;
        if (segments <= 0) segments = RecommendSegments(radius);
        var y = center.Y;

        // 全頂点を世界→画面に投影。**1 つでも失敗（カメラ外 / 真後ろ）したら描画ごとスキップ**。
        //
        // 旧実装：投影成功点だけ PathLineTo して PathFillConvex に渡していたが、
        // カメラ境界で点が飛び飛びに残ると非凸パスになり「W 字塗り潰しアーティファクト」が
        // ImGui の三角分割で発生する致命バグだった。
        // 「画面端で AoE 全体が一瞬消える」副作用はあるが、誤描画よりは遥かに安全。
        var pts = ArrayPool<Vector2>.Shared.Rent(segments);
        try
        {
            var allOk = true;
            for (var i = 0; i < segments; i++)
            {
                var t = i * MathF.PI * 2f / segments;
                var p = new Vector3(
                    center.X + radius * MathF.Cos(t), y,
                    center.Z + radius * MathF.Sin(t));
                if (!gameGui.WorldToScreen(p, out var s))
                {
                    allOk = false;
                    break;
                }
                pts[i] = s;
            }
            if (!allOk) return;

            draw.PathClear();
            for (var i = 0; i < segments; i++) draw.PathLineTo(pts[i]);
            draw.PathFillConvex(fillColor);

            draw.PathClear();
            for (var i = 0; i < segments; i++) draw.PathLineTo(pts[i]);
            draw.PathStroke(strokeColor, ImDrawFlags.Closed, 2f);
        }
        finally
        {
            ArrayPool<Vector2>.Shared.Return(pts);
        }
    }

    /// <summary>
    /// 床面に貼り付くドーナツ（中央安置・外周危険系）。
    /// 内輪と外輪の対応点で 4 角形を 1 周分塗る Splatoon 流。
    /// </summary>
    public static void DrawDonut(
        ImDrawListPtr draw, IGameGui gameGui,
        Vector3 center, float innerRadius, float outerRadius, uint fillColor, int segments = 0)
    {
        if (outerRadius <= innerRadius || outerRadius < 0.1f) return;
        if (segments <= 0) segments = RecommendSegments(outerRadius);
        var y = center.Y;

        for (var i = 0; i < segments; i++)
        {
            var t1 = i * MathF.PI * 2f / segments;
            var t2 = ((i + 1) % segments) * MathF.PI * 2f / segments;
            var pInner1 = new Vector3(center.X + innerRadius * MathF.Cos(t1), y, center.Z + innerRadius * MathF.Sin(t1));
            var pInner2 = new Vector3(center.X + innerRadius * MathF.Cos(t2), y, center.Z + innerRadius * MathF.Sin(t2));
            var pOuter1 = new Vector3(center.X + outerRadius * MathF.Cos(t1), y, center.Z + outerRadius * MathF.Sin(t1));
            var pOuter2 = new Vector3(center.X + outerRadius * MathF.Cos(t2), y, center.Z + outerRadius * MathF.Sin(t2));
            if (!gameGui.WorldToScreen(pInner1, out var sInner1)) continue;
            if (!gameGui.WorldToScreen(pInner2, out var sInner2)) continue;
            if (!gameGui.WorldToScreen(pOuter1, out var sOuter1)) continue;
            if (!gameGui.WorldToScreen(pOuter2, out var sOuter2)) continue;
            draw.PathClear();
            draw.PathLineTo(sInner1);
            draw.PathLineTo(sOuter1);
            draw.PathLineTo(sOuter2);
            draw.PathLineTo(sInner2);
            draw.PathFillConvex(fillColor);
        }
    }

    /// <summary>
    /// 床面に貼り付く扇形。中心 → 円弧上の点列 → 中心 で 1 つの凸多角形を作って塗る。
    /// </summary>
    /// <param name="startRad">扇の開始角度（ラジアン）</param>
    /// <param name="endRad">扇の終了角度（ラジアン）</param>
    public static void DrawCone(
        ImDrawListPtr draw, IGameGui gameGui,
        Vector3 center, float radius, float startRad, float endRad, uint fillColor, int segments = 0)
    {
        if (radius < 0.1f) return;
        if (segments <= 0) segments = RecommendSegments(radius);
        var y = center.Y;
        // 扇は周長の比率に応じて分割（半周なら半分、四分円なら 1/4）
        var arcFraction = Math.Min(1f, MathF.Abs(endRad - startRad) / (MathF.PI * 2f));
        var arcSegs = Math.Max(12, (int)(segments * arcFraction));

        // 全頂点投影成功時のみ凸塗り。1 つでも失敗（カメラ外）したら描画スキップ。
        // 旧実装は飛び飛び点で非凸パスを生成し W 字塗り潰しアーティファクトを誘発していた。
        var totalPts = arcSegs + 2; // center + arc points
        var pts = ArrayPool<Vector2>.Shared.Rent(totalPts);
        try
        {
            if (!gameGui.WorldToScreen(center, out var sc)) return;
            pts[0] = sc;
            for (var i = 0; i <= arcSegs; i++)
            {
                var t = startRad + (endRad - startRad) * (i / (float)arcSegs);
                var p = new Vector3(
                    center.X + radius * MathF.Cos(t), y,
                    center.Z + radius * MathF.Sin(t));
                if (!gameGui.WorldToScreen(p, out var s)) return;
                pts[i + 1] = s;
            }

            draw.PathClear();
            for (var i = 0; i < totalPts; i++) draw.PathLineTo(pts[i]);
            draw.PathFillConvex(fillColor);
        }
        finally
        {
            ArrayPool<Vector2>.Shared.Return(pts);
        }
    }

    /// <summary>
    /// 中心 origin から ±halfLength × ±halfWidth に広がる対称矩形。
    /// 十字や半面攻撃の要素として再利用できる基本図形。
    /// </summary>
    public static void DrawCenteredRect(
        ImDrawListPtr draw, IGameGui gameGui,
        Vector3 center, float halfLength, float halfWidth, float facingRad,
        uint fillColor, uint strokeColor)
    {
        if (halfLength < 0.1f || halfWidth < 0.05f) return;
        var y = center.Y;
        var fX = MathF.Cos(facingRad);
        var fZ = MathF.Sin(facingRad);
        var pX = -fZ;
        var pZ = fX;
        var p1 = new Vector3(center.X - fX * halfLength + pX * halfWidth, y, center.Z - fZ * halfLength + pZ * halfWidth);
        var p2 = new Vector3(center.X + fX * halfLength + pX * halfWidth, y, center.Z + fZ * halfLength + pZ * halfWidth);
        var p3 = new Vector3(center.X + fX * halfLength - pX * halfWidth, y, center.Z + fZ * halfLength - pZ * halfWidth);
        var p4 = new Vector3(center.X - fX * halfLength - pX * halfWidth, y, center.Z - fZ * halfLength - pZ * halfWidth);

        // 全 4 頂点投影成功時のみ凸塗り。1 つでも失敗（カメラ外）したら描画スキップ。
        // 旧実装は 3 点フォールバック → 非凸三角形が出て「W 字 / X 崩れ」のアーティファクト発生。
        // DrawCross が DrawCenteredRect を 2 回呼ぶ構造のため、片腕のフォールバックで
        // 「X を崩した W 字」が画面に出ていた。
        if (!gameGui.WorldToScreen(p1, out var s1)) return;
        if (!gameGui.WorldToScreen(p2, out var s2)) return;
        if (!gameGui.WorldToScreen(p3, out var s3)) return;
        if (!gameGui.WorldToScreen(p4, out var s4)) return;

        Span<Vector2> pts = stackalloc Vector2[4];
        pts[0] = s1; pts[1] = s2; pts[2] = s3; pts[3] = s4;

        draw.PathClear();
        for (var i = 0; i < 4; i++) draw.PathLineTo(pts[i]);
        draw.PathFillConvex(fillColor);
        draw.PathClear();
        for (var i = 0; i < 4; i++) draw.PathLineTo(pts[i]);
        draw.PathStroke(strokeColor, ImDrawFlags.Closed, 2f);
    }

    /// <summary>
    /// 十字 AoE。中心点に直交する 2 本の rect を重ねる。FFXIV の cross 系ギミック用。
    /// </summary>
    /// <param name="halfLength">各腕の中心からの長さ（m）</param>
    /// <param name="halfWidth">各腕の半幅（m）</param>
    public static void DrawCross(
        ImDrawListPtr draw, IGameGui gameGui,
        Vector3 center, float halfLength, float halfWidth, float facingRad,
        uint fillColor, uint strokeColor)
    {
        // 縦腕
        DrawCenteredRect(draw, gameGui, center, halfLength, halfWidth, facingRad, fillColor, strokeColor);
        // 横腕（直交）
        DrawCenteredRect(draw, gameGui, center, halfLength, halfWidth, facingRad + MathF.PI / 2f, fillColor, strokeColor);
    }

    /// <summary>
    /// 床面に貼り付く扇ドーナツ（cone から内径を引いた形）。
    /// FFXIV の「扇形ドーナツ AoE」（外周扇＋内側安置）に対応。
    /// </summary>
    /// <param name="startRad">開始角度</param>
    /// <param name="endRad">終了角度</param>
    public static void DrawDonutCone(
        ImDrawListPtr draw, IGameGui gameGui,
        Vector3 center, float innerRadius, float outerRadius, float startRad, float endRad, uint fillColor,
        int segments = 0)
    {
        if (outerRadius <= innerRadius || outerRadius < 0.1f) return;
        if (segments <= 0) segments = RecommendSegments(outerRadius);
        var y = center.Y;
        var arcFraction = Math.Min(1f, MathF.Abs(endRad - startRad) / (MathF.PI * 2f));
        var arcSegs = Math.Max(12, (int)(segments * arcFraction));
        var span = endRad - startRad;

        // 各サブセグメントを「内輪 1, 外輪 1, 外輪 2, 内輪 2」の四角形で塗る（DrawDonut 同じ方式）
        for (var i = 0; i < arcSegs; i++)
        {
            var t1 = startRad + span * (i / (float)arcSegs);
            var t2 = startRad + span * ((i + 1) / (float)arcSegs);
            var pInner1 = new Vector3(center.X + innerRadius * MathF.Cos(t1), y, center.Z + innerRadius * MathF.Sin(t1));
            var pInner2 = new Vector3(center.X + innerRadius * MathF.Cos(t2), y, center.Z + innerRadius * MathF.Sin(t2));
            var pOuter1 = new Vector3(center.X + outerRadius * MathF.Cos(t1), y, center.Z + outerRadius * MathF.Sin(t1));
            var pOuter2 = new Vector3(center.X + outerRadius * MathF.Cos(t2), y, center.Z + outerRadius * MathF.Sin(t2));
            if (!gameGui.WorldToScreen(pInner1, out var sInner1)) continue;
            if (!gameGui.WorldToScreen(pInner2, out var sInner2)) continue;
            if (!gameGui.WorldToScreen(pOuter1, out var sOuter1)) continue;
            if (!gameGui.WorldToScreen(pOuter2, out var sOuter2)) continue;
            draw.PathClear();
            draw.PathLineTo(sInner1);
            draw.PathLineTo(sOuter1);
            draw.PathLineTo(sOuter2);
            draw.PathLineTo(sInner2);
            draw.PathFillConvex(fillColor);
        }
    }

    /// <summary>
    /// 半面攻撃。center から facingRad 方向に planeLength の長辺を持つ大きな矩形。
    /// アリーナ 1 辺を覆う左右どちらかの半面ギミック用。<see cref="DrawCenteredRect"/> の
    /// 半幅をアリーナ寸法に合わせて呼ぶ別エイリアス。
    /// </summary>
    public static void DrawHalfPlane(
        ImDrawListPtr draw, IGameGui gameGui,
        Vector3 center, float facingRad, float halfWidth, float planeLength,
        uint fillColor, uint strokeColor)
    {
        // 半面 = forward 方向に planeLength（前 ＋ 後ろ両方塗りたい場合は呼び出し側で 2 回呼ぶ）。
        // 実態は片側に伸びる長 rect なので DrawRect に転送（origin = center, length = planeLength）。
        DrawRect(draw, gameGui, center, planeLength, halfWidth, facingRad, fillColor, strokeColor);
    }

    /// <summary>
    /// 床面に貼り付く矩形 AoE（CastType 4 / 直線クリーブ用）。
    /// 原点 origin から forward 方向に length メートル、垂直に halfWidth × 2 の幅。
    /// </summary>
    /// <param name="facingRad">中心軸方向（0=+X 東, π/2=+Z 南, π=-X 西, -π/2=-Z 北）</param>
    public static void DrawRect(
        ImDrawListPtr draw, IGameGui gameGui,
        Vector3 origin, float length, float halfWidth, float facingRad,
        uint fillColor, uint strokeColor)
    {
        if (length < 0.1f || halfWidth < 0.05f) return;
        var y = origin.Y;
        var forwardX = MathF.Cos(facingRad);
        var forwardZ = MathF.Sin(facingRad);
        var perpX = -forwardZ;
        var perpZ = forwardX;
        var p1 = new Vector3(origin.X + perpX * halfWidth, y, origin.Z + perpZ * halfWidth);
        var p2 = new Vector3(origin.X + forwardX * length + perpX * halfWidth, y, origin.Z + forwardZ * length + perpZ * halfWidth);
        var p3 = new Vector3(origin.X + forwardX * length - perpX * halfWidth, y, origin.Z + forwardZ * length - perpZ * halfWidth);
        var p4 = new Vector3(origin.X - perpX * halfWidth, y, origin.Z - perpZ * halfWidth);

        // 全 4 頂点投影成功時のみ凸塗り。失敗時は非凸ポリゴンを作らず、中心線だけに退避する。
        if (!gameGui.WorldToScreen(p1, out var s1) ||
            !gameGui.WorldToScreen(p2, out var s2) ||
            !gameGui.WorldToScreen(p3, out var s3) ||
            !gameGui.WorldToScreen(p4, out var s4))
        {
            DrawRectCenterlineFallback(draw, gameGui, origin, length, facingRad, fillColor, strokeColor);
            return;
        }

        Span<Vector2> pts = stackalloc Vector2[4];
        pts[0] = s1; pts[1] = s2; pts[2] = s3; pts[3] = s4;

        draw.PathClear();
        for (var i = 0; i < 4; i++) draw.PathLineTo(pts[i]);
        draw.PathFillConvex(fillColor);

        draw.PathClear();
        for (var i = 0; i < 4; i++) draw.PathLineTo(pts[i]);
        draw.PathStroke(strokeColor, ImDrawFlags.Closed, 2f);
    }

    private static void DrawRectCenterlineFallback(
        ImDrawListPtr draw,
        IGameGui gameGui,
        Vector3 origin,
        float length,
        float facingRad,
        uint fillColor,
        uint strokeColor)
    {
        var forwardX = MathF.Cos(facingRad);
        var forwardZ = MathF.Sin(facingRad);
        var y = origin.Y;
        var hasLast = false;
        var last = Vector2.Zero;
        for (var i = 0; i <= 8; i++)
        {
            var t = i / 8f;
            var p = new Vector3(
                origin.X + forwardX * length * t,
                y,
                origin.Z + forwardZ * length * t);
            if (!gameGui.WorldToScreen(p, out var screen))
            {
                hasLast = false;
                continue;
            }

            if (hasLast)
            {
                draw.AddLine(last, screen, fillColor, 12f);
                draw.AddLine(last, screen, strokeColor, 3f);
            }
            else
            {
                draw.AddCircleFilled(screen, 4f, strokeColor, 12);
            }

            last = screen;
            hasLast = true;
        }
    }
}
