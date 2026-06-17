using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FfxivEchoes.Diagnostics;

namespace FfxivEchoes.Windows;

/// <summary>
/// ワールド座標を画面に投影して描画する透明オーバーレイ。
/// screen_arrow / field_marker の描画先（SPEC.md §5.1）。
/// </summary>
public sealed class WorldOverlayWindow : Window, IDisposable
{
    private readonly IGameGui _gameGui;
    private readonly IObjectTable _objectTable;
    private readonly List<ArrowItem> _arrows = new();
    private readonly List<MarkerItem> _markers = new();
    private readonly List<Action<ImDrawListPtr, IGameGui>> _liveDrawers = new();
    private readonly object _gate = new();

    public WorldOverlayWindow(IGameGui gameGui, IObjectTable objectTable)
        : base("##ffxiv-echoes-world-overlay",
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoBringToFrontOnFocus |
            ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoMouseInputs |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoSavedSettings)
    {
        _gameGui = gameGui;
        _objectTable = objectTable;
        IsOpen = false;
        RespectCloseHotkey = false;
    }

    public void Dispose() { }

    /// <summary>
    /// 毎フレーム呼ばれるライブ描画コールバックを登録する。
    /// Splatoon 流のステートレス再評価向け：登録者が <see cref="ImDrawListPtr"/> に直接描く。
    /// </summary>
    /// <returns>解除用 IDisposable。Dispose されるまで毎フレーム呼ばれる。</returns>
    public IDisposable RegisterLiveDrawer(Action<ImDrawListPtr, IGameGui> drawer)
    {
        lock (_gate)
        {
            _liveDrawers.Add(drawer);
        }
        IsOpen = true;
        return new DrawerToken(this, drawer);
    }

    private void RemoveLiveDrawer(Action<ImDrawListPtr, IGameGui> drawer)
    {
        lock (_gate)
        {
            _liveDrawers.Remove(drawer);
            // 描画対象が一切無くなったら次フレーム以降は Draw を呼ばない（無駄な
            // フレーム処理を避ける。1ms/frame の予算内に収めるための配慮）。
            if (_liveDrawers.Count == 0 && _arrows.Count == 0 && _markers.Count == 0)
            {
                IsOpen = false;
            }
        }
    }

    private sealed class DrawerToken : IDisposable
    {
        private readonly WorldOverlayWindow _owner;
        private readonly Action<ImDrawListPtr, IGameGui> _drawer;
        private bool _disposed;

        public DrawerToken(WorldOverlayWindow owner, Action<ImDrawListPtr, IGameGui> drawer)
        {
            _owner = owner;
            _drawer = drawer;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner.RemoveLiveDrawer(_drawer);
        }
    }

    public void AddArrow(bool fromSelf, Vector3? from, Vector3 to, string? colorHex, double durationSec)
    {
        var ttl = durationSec <= 0 ? 5.0 : durationSec;
        var item = new ArrowItem(
            FromSelf: fromSelf,
            From: from ?? Vector3.Zero,
            To: to,
            Color: ParseColor(colorHex, 0xFF00FF00),
            ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(ttl));
        lock (_gate)
        {
            _arrows.Add(item);
        }
        IsOpen = true;
    }

    public void AddMarker(Vector3 worldPos, string? shape, float radius, string? colorHex, double durationSec)
    {
        var ttl = durationSec <= 0 ? 5.0 : durationSec;
        var item = new MarkerItem(
            WorldPos: worldPos,
            Shape: (shape ?? "circle").ToLowerInvariant(),
            Radius: MathF.Max(0.5f, radius),
            InnerRadius: 0f,
            ConeStartRad: 0f,
            ConeEndRad: 0f,
            Color: ParseColor(colorHex, 0xFF00FF00),
            ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(ttl));
        lock (_gate)
        {
            _markers.Add(item);
        }
        IsOpen = true;
    }

    /// <summary>
    /// 床面に貼り付くドーナツ AoE（Splatoon スタイルの世界空間描画）。
    /// 内径 innerRadius、外径 outerRadius の環状塗り。
    /// </summary>
    public void AddDonut(Vector3 worldPos, float innerRadius, float outerRadius, string? colorHex, double durationSec)
    {
        var ttl = durationSec <= 0 ? 5.0 : durationSec;
        lock (_gate)
        {
            _markers.Add(new MarkerItem(
                WorldPos: worldPos,
                Shape: "world_donut",
                Radius: MathF.Max(0.5f, outerRadius),
                InnerRadius: MathF.Max(0f, innerRadius),
                ConeStartRad: 0f,
                ConeEndRad: 0f,
                Color: ParseColor(colorHex, 0xFFFF6464),
                ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(ttl)));
        }
        IsOpen = true;
    }

    /// <summary>
    /// 床面に貼り付く扇形 AoE（Splatoon スタイル）。
    /// </summary>
    /// <param name="facingRad">扇の中心方向（ラジアン、0=東 / π/2=南 / π=西 / -π/2=北）</param>
    /// <param name="fanDeg">扇の広がり角度（degree）</param>
    public void AddCone(Vector3 worldPos, float radius, float facingRad, float fanDeg, string? colorHex, double durationSec)
    {
        var ttl = durationSec <= 0 ? 5.0 : durationSec;
        var halfFan = (float)(fanDeg * Math.PI / 180.0 * 0.5);
        lock (_gate)
        {
            _markers.Add(new MarkerItem(
                WorldPos: worldPos,
                Shape: "world_cone",
                Radius: MathF.Max(0.5f, radius),
                InnerRadius: 0f,
                ConeStartRad: facingRad - halfFan,
                ConeEndRad: facingRad + halfFan,
                Color: ParseColor(colorHex, 0xFFFF6464),
                ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(ttl)));
        }
        IsOpen = true;
    }

    /// <summary>
    /// 床面に貼り付く塗りつぶし円 AoE（Splatoon スタイル）。
    /// `AddMarker` の "circle" は flat 2D 円なので別 API として用意。
    /// </summary>
    public void AddWorldCircle(Vector3 worldPos, float radius, string? colorHex, double durationSec)
    {
        var ttl = durationSec <= 0 ? 5.0 : durationSec;
        lock (_gate)
        {
            _markers.Add(new MarkerItem(
                WorldPos: worldPos,
                Shape: "world_circle",
                Radius: MathF.Max(0.5f, radius),
                InnerRadius: 0f,
                ConeStartRad: 0f,
                ConeEndRad: 0f,
                Color: ParseColor(colorHex, 0xFFFF6464),
                ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(ttl)));
        }
        IsOpen = true;
    }

    /// <summary>
    /// caster-anchored の矩形 AoE（CastType 4 line / 矩形クリーブ用）。
    /// 原点 origin から facingRad 方向に length（m）、進行方向と垂直に
    /// halfWidth × 2 の幅。Splatoon の DrawRectWorld 相当。
    /// </summary>
    /// <param name="facingRad">中心軸方向（ラジアン、0=東 / π/2=南 / π=西 / -π/2=北）</param>
    public void AddWorldRect(Vector3 origin, float length, float halfWidth, float facingRad, string? colorHex, double durationSec)
    {
        var ttl = durationSec <= 0 ? 5.0 : durationSec;
        lock (_gate)
        {
            _markers.Add(new MarkerItem(
                WorldPos: origin,
                Shape: "world_rect",
                Radius: MathF.Max(0.5f, length),       // 進行方向長さ
                InnerRadius: MathF.Max(0.1f, halfWidth), // 進行方向に直交する半幅
                ConeStartRad: facingRad,                 // 中心軸方向
                ConeEndRad: 0f,
                Color: ParseColor(colorHex, 0xFFFF6464),
                ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(ttl)));
        }
        IsOpen = true;
    }

    /// <summary>
    /// 「from から to への直線 AoE」を矩形で表現する糖衣 API。
    /// 内部では from を origin、from→to の角度を facingRad、|to-from| を length として
    /// <see cref="AddWorldRect"/> に転送する。タンクを射線にする線型 AoE などに有用。
    /// </summary>
    public void AddWorldLine(Vector3 from, Vector3 to, float halfWidth, string? colorHex, double durationSec)
    {
        var dx = to.X - from.X;
        var dz = to.Z - from.Z;
        var length = MathF.Sqrt(dx * dx + dz * dz);
        if (length < 0.5f)
        {
            // 距離ゼロは描けない。直線の意味が無い。
            return;
        }
        var facingRad = MathF.Atan2(dz, dx);
        AddWorldRect(from, length, halfWidth, facingRad, colorHex, durationSec);
    }

    public override void PreDraw()
    {
        // 全画面サイズに展開
        var io = ImGui.GetIO();
        ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
        ImGui.SetNextWindowSize(io.DisplaySize, ImGuiCond.Always);
    }

    public override void Draw()
    {
        try
        {
            DrawCore();
        }
        catch (Exception ex)
        {
            FrameErrorThrottle.Report(Plugin.Log, ex, "WorldOverlayWindow.Draw");
        }
    }

    private void DrawCore()
    {
        var now = DateTimeOffset.UtcNow;
        ArrowItem[] arrows;
        MarkerItem[] markers;
        Action<ImDrawListPtr, IGameGui>[] drawers;
        lock (_gate)
        {
            _arrows.RemoveAll(a => a.ExpiresAt <= now);
            _markers.RemoveAll(m => m.ExpiresAt <= now);
            arrows = _arrows.ToArray();
            markers = _markers.ToArray();
            drawers = _liveDrawers.ToArray();
        }

        // 描画対象が一切なければウィンドウ自身を閉じて Draw を抑制。
        // ライブドロワーは「登録されているだけで毎フレーム描画したい」ので、
        // 登録があれば必ず開いておく。
        if (arrows.Length == 0 && markers.Length == 0 && drawers.Length == 0)
        {
            IsOpen = false;
            return;
        }

        var draw = ImGui.GetForegroundDrawList();

        var localPlayer = _objectTable.LocalPlayer;
        var selfWorld = localPlayer is not null
            ? new Vector3(localPlayer.Position.X, localPlayer.Position.Y, localPlayer.Position.Z)
            : Vector3.Zero;

        foreach (var arrow in arrows)
        {
            var fromWorld = arrow.FromSelf ? selfWorld : arrow.From;
            if (!_gameGui.WorldToScreen(fromWorld, out var fromScreen)) continue;
            if (!_gameGui.WorldToScreen(arrow.To, out var toScreen)) continue;
            DrawArrow(draw, fromScreen, toScreen, arrow.Color);
        }

        foreach (var marker in markers)
        {
            // Splatoon スタイル：床面に貼り付く塗りつぶし AoE。世界座標で円周を
            // サンプリング → 各点を WorldToScreen 投影 → ImGui の path API で塗る。
            // これによりカメラ回転や床のパースに合わせて AoE が「床に塗ったように」見える。
            if (marker.Shape == "world_circle" || marker.Shape == "world_donut" ||
                marker.Shape == "world_cone" || marker.Shape == "world_rect")
            {
                DrawWorldShape(draw, _gameGui, marker);
                continue;
            }

            if (!_gameGui.WorldToScreen(marker.WorldPos, out var center)) continue;
            // 半径をスクリーン画素数に変換：中心 + 水平 N メートル の点を投影して画素距離を取る
            float pixelRadius;
            var edgeWorld = marker.WorldPos + new Vector3(marker.Radius, 0, 0);
            if (_gameGui.WorldToScreen(edgeWorld, out var edge))
            {
                pixelRadius = (edge - center).Length();
            }
            else
            {
                pixelRadius = MathF.Max(20f, marker.Radius * 8f); // フォールバック
            }
            DrawMarker(draw, center, pixelRadius, marker);
        }

        // ライブドロワー：Splatoon 流ステートレス再評価のクライアント側ロジックが
        // 毎フレーム自前で描く。例外は握り潰す（1 つのドロワーが他を巻き込まない）。
        foreach (var d in drawers)
        {
            try
            {
                d(draw, _gameGui);
            }
            catch
            {
                // 描画失敗を黙殺。ログは呼び出し側で。
            }
        }
    }

    /// <summary>
    /// Splatoon 系の世界座標多角形描画。実体は <see cref="WorldShapeRenderer"/> に委譲。
    /// </summary>
    private static void DrawWorldShape(ImDrawListPtr draw, IGameGui gameGui, MarkerItem m)
    {
        var fillColor = (m.Color & 0x00FFFFFFu) | 0x60000000u;
        var strokeColor = (m.Color & 0x00FFFFFFu) | 0xE0000000u;

        switch (m.Shape)
        {
            case "world_circle":
                WorldShapeRenderer.DrawCircle(draw, gameGui, m.WorldPos, m.Radius, fillColor, strokeColor);
                break;
            case "world_donut":
                WorldShapeRenderer.DrawDonut(draw, gameGui, m.WorldPos, m.InnerRadius, m.Radius, fillColor);
                break;
            case "world_cone":
                WorldShapeRenderer.DrawCone(draw, gameGui, m.WorldPos, m.Radius, m.ConeStartRad, m.ConeEndRad, fillColor);
                break;
            case "world_rect":
                // ConeStartRad を facingRad、InnerRadius を halfWidth として再利用
                WorldShapeRenderer.DrawRect(draw, gameGui, m.WorldPos, m.Radius, m.InnerRadius, m.ConeStartRad, fillColor, strokeColor);
                break;
        }
    }

    private static void DrawArrow(ImDrawListPtr draw, Vector2 from, Vector2 to, uint color)
    {
        draw.AddLine(from, to, color, 3f);
        // 簡易矢じり（直線の終端）
        var dir = to - from;
        var len = dir.Length();
        if (len < 1f) return;
        dir /= len;
        var perp = new Vector2(-dir.Y, dir.X);
        const float headLen = 14f;
        const float headWidth = 8f;
        var tip = to;
        var baseCenter = to - dir * headLen;
        var left = baseCenter + perp * headWidth;
        var right = baseCenter - perp * headWidth;
        draw.AddTriangleFilled(tip, left, right, color);
    }

    private static void DrawMarker(ImDrawListPtr draw, Vector2 center, float pixelRadius, MarkerItem marker)
    {
        // 円が大き過ぎる場合は描画 segment 数を増やす。最低 24、最大 64
        var segments = (int)MathF.Min(64, MathF.Max(24, pixelRadius * 0.4f));
        // 半透明塗り
        var fillColor = (marker.Color & 0x00FFFFFF) | 0x40000000;
        switch (marker.Shape)
        {
            case "x_mark":
            {
                var px = MathF.Max(20f, pixelRadius * 0.4f);
                draw.AddLine(center + new Vector2(-px, -px), center + new Vector2(px, px), marker.Color, 3f);
                draw.AddLine(center + new Vector2(-px, px), center + new Vector2(px, -px), marker.Color, 3f);
                break;
            }
            case "square":
            {
                var px = pixelRadius;
                draw.AddRectFilled(center + new Vector2(-px, -px), center + new Vector2(px, px), fillColor);
                draw.AddRect(center + new Vector2(-px, -px), center + new Vector2(px, px), marker.Color, 0f, ImDrawFlags.None, 3f);
                break;
            }
            case "arrow":
            {
                var px = MathF.Max(20f, pixelRadius * 0.5f);
                draw.AddTriangleFilled(
                    center + new Vector2(0, -px),
                    center + new Vector2(-px * 0.7f, px * 0.5f),
                    center + new Vector2(px * 0.7f, px * 0.5f),
                    marker.Color);
                break;
            }
            case "circle":
            default:
            {
                // 半径が画面外に出るほど大きい場合のために、min/max 制限
                var r = MathF.Max(8f, pixelRadius);
                draw.AddCircleFilled(center, r, fillColor, segments);
                draw.AddCircle(center, r, marker.Color, segments, 3f);
                draw.AddCircleFilled(center, 5f, marker.Color); // 中心点
                break;
            }
        }
    }

    private static uint ParseColor(string? color, uint fallback)
    {
        if (string.IsNullOrEmpty(color) || color.Length != 7 || color[0] != '#')
        {
            return fallback;
        }
        if (!uint.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return fallback;
        }
        var r = (rgb >> 16) & 0xFF;
        var g = (rgb >> 8) & 0xFF;
        var b = rgb & 0xFF;
        // ImGui は ABGR
        return (0xFFu << 24) | (b << 16) | (g << 8) | r;
    }

    private sealed record ArrowItem(bool FromSelf, Vector3 From, Vector3 To, uint Color, DateTimeOffset ExpiresAt);
    private sealed record MarkerItem(
        Vector3 WorldPos,
        string Shape,
        float Radius,
        float InnerRadius,
        float ConeStartRad,
        float ConeEndRad,
        uint Color,
        DateTimeOffset ExpiresAt);
}
