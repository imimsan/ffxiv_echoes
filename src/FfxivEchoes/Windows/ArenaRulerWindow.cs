using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FfxivEchoes.Capture;

namespace FfxivEchoes.Windows;

/// <summary>
/// アリーナの実寸を測るためのライブ計測ツール。
/// </summary>
/// <remarks>
/// <para>
/// 「ゲーム内のマップが何メートルか」をプレイヤーに見せるための窓。
/// 自分位置の世界座標、設置済みウェイマークの世界座標、マーカー間距離を
/// フレーム毎に更新表示。校正の手順（A と D を対辺に置く → 距離 = 幅）を
/// そのまま読み取れる UI にする。
/// </para>
/// <para>
/// 加えて <see cref="DrawWorldOverlay"/> で自分中心の 5m きざみ同心円を
/// 世界空間に投影描画する（実ゲーム画面に重ねる）。
/// 「自分から 5m はここ」が画面で見えるので寸法感覚が掴める。
/// </para>
/// </remarks>
public sealed class ArenaRulerWindow : Window, IDisposable
{
    private readonly IObjectTable _objectTable;
    private readonly IGameGui _gameGui;

    private bool _showWorldOverlay = true;

    public ArenaRulerWindow(IObjectTable objectTable, IGameGui gameGui)
        : base("アリーナ寸法計測##ffxiv-echoes-arena-ruler",
            ImGuiWindowFlags.NoCollapse)
    {
        _objectTable = objectTable;
        _gameGui = gameGui;
        Size = new Vector2(360f, 0f);
        SizeCondition = ImGuiCond.FirstUseEver;
        IsOpen = false;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var lp = _objectTable.LocalPlayer;
        if (lp is null)
        {
            ImGui.TextDisabled("自分が居ない（ログイン中？）");
            return;
        }
        var selfPos = new Vector3(lp.Position.X, lp.Position.Y, lp.Position.Z);

        ImGui.TextUnformatted("自分の世界座標");
        ImGui.Indent();
        ImGui.TextColored(new Vector4(1f, 0.85f, 0.4f, 1f),
            $"X = {selfPos.X:0.00}");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(1f, 0.85f, 0.4f, 1f),
            $"  Z = {selfPos.Z:0.00}");
        ImGui.SameLine();
        ImGui.TextDisabled($"  (Y={selfPos.Y:0.0})");
        ImGui.Unindent();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("ウェイマーク（設置済のみ）と自分からの距離");
        var letters = new[] { "A", "B", "C", "D", "1", "2", "3", "4" };
        var positions = new Vector3?[8];
        for (var i = 0; i < 8; i++)
        {
            positions[i] = WaymarkProvider.TryGetPosition(letters[i]);
        }

        if (ImGui.BeginTable("##wm-table", 4,
                ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("マーカー", ImGuiTableColumnFlags.WidthFixed, 64f);
            ImGui.TableSetupColumn("X");
            ImGui.TableSetupColumn("Z");
            ImGui.TableSetupColumn("自分から");
            ImGui.TableHeadersRow();

            var anyPlaced = false;
            for (var i = 0; i < 8; i++)
            {
                var pos = positions[i];
                if (pos is null) continue;
                anyPlaced = true;
                var dist = Math.Sqrt(
                    Math.Pow(pos.Value.X - selfPos.X, 2) +
                    Math.Pow(pos.Value.Z - selfPos.Z, 2));
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(WaymarkColor(i), letters[i]);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{pos.Value.X:0.00}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{pos.Value.Z:0.00}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{dist:0.0} m");
            }
            ImGui.EndTable();

            if (!anyPlaced)
            {
                ImGui.TextDisabled("（マーカーが 1 つも置かれていません）");
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("マーカー間距離（アリーナ寸法の校正用）");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "練習部屋でアリーナの東西端に A と D（または C）を置くと\n" +
                "A↔D 距離 = アリーナの幅。\n" +
                "南北端には B と何かを置けば奥行が分かる。");
        }
        // 主要ペアだけ表示：A↔D（横）/ B↔C（縦）/ A↔C と B↔D（対角）
        DrawDistanceRow("A → D", positions[0], positions[3]);
        DrawDistanceRow("B → C", positions[1], positions[2]);
        DrawDistanceRow("A → C", positions[0], positions[2]);
        DrawDistanceRow("B → D", positions[1], positions[3]);
        DrawDistanceRow("A → B", positions[0], positions[1]);
        DrawDistanceRow("1 → 3", positions[4], positions[6]);
        DrawDistanceRow("2 → 4", positions[5], positions[7]);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Checkbox("自分中心の同心円を画面に重ねる (5-40 m)##ruler-overlay", ref _showWorldOverlay);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "自分の周りに 5m きざみで最大 40m 半径の円を世界空間に描画。\n" +
                "「自分から壁まで何 m か」を歩いて確認できる。\n" +
                "ウィンドウを閉じれば自動的に消える。");
        }
        ImGui.Spacing();
        ImGui.TextDisabled("使い方：");
        ImGui.TextDisabled("  1. このウィンドウを開いて練習部屋に入る");
        ImGui.TextDisabled("  2. 自分中心の同心円を見て壁までの距離を確認");
        ImGui.TextDisabled("  3. 必要なら A と D（東西）を対辺に置いて A→D = 幅");

        DrawWorldOverlay();
    }

    /// <summary>
    /// Window.Draw の中から世界空間オーバーレイを描く。
    /// ImGui の描画 API は Framework.Update から直接呼ばず、描画フレーム内に閉じる。
    /// </summary>
    public void DrawWorldOverlay()
    {
        if (!IsOpen || !_showWorldOverlay) return;
        var lp = _objectTable.LocalPlayer;
        if (lp is null) return;

        var selfWorld = new Vector3(lp.Position.X, lp.Position.Y, lp.Position.Z);
        var draw = ImGui.GetForegroundDrawList();
        var radii = ArenaRulerPolicy.RadiiMeters;
        var colors = new uint[]
        {
            0xC000FFFFu, // 5m: 黄
            0xC000FF80u, // 10m: 緑
            0xC0FF8000u, // 15m: 水色
            0xC0FF40FFu, // 20m: 紫
            0xB0FFFFFFu, // 25m
            0xB0A0A0FFu, // 30m
            0xB0FFA0A0u, // 35m
            0xB0A0FFFFu, // 40m
        };
        for (var ri = 0; ri < radii.Length; ri++)
        {
            var r = radii[ri];
            var color = colors[ri];
            const int segments = 64;
            for (var i = 0; i < segments; i++)
            {
                var a1 = (float)(i * Math.PI * 2 / segments);
                var a2 = (float)((i + 1) * Math.PI * 2 / segments);
                var p1 = new Vector3(
                    selfWorld.X + r * MathF.Cos(a1),
                    selfWorld.Y,
                    selfWorld.Z + r * MathF.Sin(a1));
                var p2 = new Vector3(
                    selfWorld.X + r * MathF.Cos(a2),
                    selfWorld.Y,
                    selfWorld.Z + r * MathF.Sin(a2));
                if (!_gameGui.WorldToScreen(p1, out var s1)) continue;
                if (!_gameGui.WorldToScreen(p2, out var s2)) continue;
                draw.AddLine(s1, s2, color, 1.5f);
            }
            // 半径ラベル：北側（Z- 方向）に「5m」「10m」… の文字を浮かべる
            var labelWorld = new Vector3(selfWorld.X, selfWorld.Y, selfWorld.Z - r);
            if (_gameGui.WorldToScreen(labelWorld, out var labelScreen))
            {
                var label = $"{r:0}m";
                var sz = ImGui.CalcTextSize(label);
                var pos = labelScreen - sz * 0.5f;
                // 縁取り（黒）→ 本体（色）
                draw.AddText(pos + new Vector2(1, 1), 0xFF000000u, label);
                draw.AddText(pos, color | 0xFF000000u, label);
            }
        }
    }

    private static void DrawDistanceRow(string label, Vector3? a, Vector3? b)
    {
        if (a is null || b is null)
        {
            ImGui.TextDisabled($"  {label}: 未設置");
            return;
        }
        var dx = a.Value.X - b.Value.X;
        var dz = a.Value.Z - b.Value.Z;
        var d = Math.Sqrt(dx * dx + dz * dz);
        ImGui.TextUnformatted($"  {label}:  {d:0.0} m   (|ΔX|={Math.Abs(dx):0.0}, |ΔZ|={Math.Abs(dz):0.0})");
    }

    private static Vector4 WaymarkColor(int idx) => idx switch
    {
        0 or 4 => new Vector4(0.91f, 0.30f, 0.24f, 1f), // 赤 A/1
        1 or 5 => new Vector4(0.95f, 0.77f, 0.06f, 1f), // 黄 B/2
        2 or 6 => new Vector4(0.20f, 0.60f, 0.86f, 1f), // 青 C/3
        3 or 7 => new Vector4(0.61f, 0.35f, 0.71f, 1f), // 紫 D/4
        _ => new Vector4(1, 1, 1, 1),
    };
}
