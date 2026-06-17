using System;
using System.Collections.Generic;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Triggers;

/// <summary>
/// よくあるギミック処理のひな形。<see cref="MechanicStrategy"/> を 1 件生成する。
/// UI から「テンプレートから作成」を選んで <see cref="StrategyProfile.Mechanics"/> に追加する。
/// </summary>
public static class MechanicTemplates
{
    /// <summary>テンプレート 1 件分の定義。</summary>
    public sealed record TemplateEntry(string Id, string Label, string Description, Func<MechanicStrategy> Build);

    private const string DefaultColor = "#F472B6";
    private const double DefaultAdvanceWarningSec = 5.0;
    private const double DefaultDuration = 5.0;

    /// <summary>
    /// テンプレートが配置に使う基準半径（m）。テンプレ内の固定座標（±14、±12 など）は
    /// 「半径 20m のアリーナ前提」で書かれている。違うサイズのアリーナに適用するときは
    /// <see cref="RescaleToArena"/> で当該プロファイル寸法に合わせて拡縮する。
    /// </summary>
    private const double TemplateReferenceHalfRadius = 20.0;

    /// <summary>
    /// テンプレ生成された <see cref="MechanicStrategy"/> の散開ポジ／オブジェクト位置／
    /// AoE 中心座標を、アリーナ寸法に合わせてスケールする。
    /// </summary>
    /// <remarks>
    /// <para>
    /// AoE のサイズ（RadiusM / InnerRadiusM / HalfWidthM）と回転（RotationDeg / FanDeg）は
    /// 実ボス技固有のメートル値なのでスケール対象外。スケールするのは「アリーナ内のどこに
    /// 配置するか」を表す座標のみ。
    /// </para>
    /// <para>
    /// スケール係数は X / Z 軸独立に <c>halfExtent / 20</c>。狭いアリーナ（halfW=10）なら
    /// 50% に縮む。広いアリーナ（halfW=30）なら 150% に広がる（ただしデフォの ±14 等は
    /// 余裕を持って書かれているのでオーバーフローしにくい）。
    /// </para>
    /// </remarks>
    public static void RescaleToArena(MechanicStrategy mechanic, double halfW, double halfD)
    {
        if (halfW <= 0 || halfD <= 0)
        {
            return;
        }
        var sx = halfW / TemplateReferenceHalfRadius;
        var sz = halfD / TemplateReferenceHalfRadius;
        if (Math.Abs(sx - 1.0) < 0.001 && Math.Abs(sz - 1.0) < 0.001)
        {
            return; // 既定（halfW=halfD=20）と同じならスケール不要
        }

        foreach (var sp in mechanic.SpreadPositions)
        {
            sp.X *= sx;
            sp.Z *= sz;
        }
        foreach (var mk in mechanic.ObjectMarkers)
        {
            mk.X *= sx;
            mk.Z *= sz;
        }
        foreach (var aoe in mechanic.AoeZones)
        {
            // AoE 中心の配置だけスケール。RadiusM 等の AoE サイズはボス技の現実値なので不変。
            aoe.X *= sx;
            aoe.Z *= sz;
        }
    }

    public static IReadOnlyList<TemplateEntry> All { get; } = new[]
    {
        new TemplateEntry(
            "eight_way_spread",
            "8 方向散開",
            "MT/ST/H1/H2/D1-D4 の標準 8 方向散開ポジ",
            BuildEightWaySpread),
        new TemplateEntry(
            "role_stack_pair",
            "ロールペア集合",
            "タンク同士・ヒラ同士・DPS 4 人で集まる 3 グループ",
            BuildRoleStackPair),
        new TemplateEntry(
            "donut_dodge",
            "ドーナツ回避（中央集合）",
            "中央安置のドーナツ AoE + 全員中央へ集合",
            BuildDonutDodge),
        new TemplateEntry(
            "inner_circle_dodge",
            "中央 AoE 回避（外周散開）",
            "中心に円形 AoE + 8 方向に外周散開",
            BuildInnerCircleDodge),
        new TemplateEntry(
            "two_side_cleave",
            "両翼攻撃回避（前後安置）",
            "ボス左右に扇 AoE + 前後 2 群に分かれる",
            BuildTwoSideCleave),
        new TemplateEntry(
            "tower_assignments",
            "タワー処理（4 タワー想定）",
            "アリーナ N/E/S/W にタワー + 担当 PT 配置",
            BuildTowerAssignments),
        new TemplateEntry(
            "tank_swap",
            "タンクスワップ",
            "MT/ST 入れ替え。callout = スイッチ",
            BuildTankSwap),
    };

    private static MechanicStrategy CreateBase(string label)
    {
        return new MechanicStrategy
        {
            Label = label,
            Enabled = true,
            AdvanceWarningSec = DefaultAdvanceWarningSec,
            Duration = DefaultDuration,
            Color = DefaultColor,
        };
    }

    private static List<StrategyPosition> EightWayPositions()
    {
        return new List<StrategyPosition>
        {
            new() { Slot = "MT", Label = "MT", Role = "tank",   X = 0,   Z = -14, Color = "#60A5FA" },
            new() { Slot = "ST", Label = "ST", Role = "tank",   X = 0,   Z = 14,  Color = "#60A5FA" },
            new() { Slot = "H1", Label = "H1", Role = "healer", X = -14, Z = 0,   Color = "#34D399" },
            new() { Slot = "H2", Label = "H2", Role = "healer", X = 14,  Z = 0,   Color = "#34D399" },
            new() { Slot = "D1", Label = "D1", Role = "dps",    X = -10, Z = -10, Color = "#F87171" },
            new() { Slot = "D2", Label = "D2", Role = "dps",    X = 10,  Z = -10, Color = "#F87171" },
            new() { Slot = "D3", Label = "D3", Role = "dps",    X = -10, Z = 10,  Color = "#FBBF24" },
            new() { Slot = "D4", Label = "D4", Role = "dps",    X = 10,  Z = 10,  Color = "#FBBF24" },
        };
    }

    private static MechanicStrategy BuildEightWaySpread()
    {
        var m = CreateBase("8 方向散開");
        m.Callout = "散開";
        m.WarningText = "散開準備";
        // Gimmick = null とし、user_layout 経路で SpreadPositions だけ描く。
        // 旧 "spread" gimmick はハードコード描画のため AoE と二重表示になる。
        m.SpreadPositions = EightWayPositions();
        return m;
    }

    private static MechanicStrategy BuildRoleStackPair()
    {
        var m = CreateBase("ロールペア集合");
        m.Callout = "ロール集合";
        m.WarningText = "ロール毎に集合";
        // Gimmick = null：散開ポジだけ描く（旧 "stack" gimmick のハードコード AoE 描画を避ける）
        // タンク 2 人は北、ヒラ 2 人は南、DPS 4 人は中央。
        m.SpreadPositions = new List<StrategyPosition>
        {
            new() { Slot = "MT", Label = "MT", Role = "tank",   X = -2, Z = -10, Color = "#60A5FA" },
            new() { Slot = "ST", Label = "ST", Role = "tank",   X = 2,  Z = -10, Color = "#60A5FA" },
            new() { Slot = "H1", Label = "H1", Role = "healer", X = -2, Z = 10,  Color = "#34D399" },
            new() { Slot = "H2", Label = "H2", Role = "healer", X = 2,  Z = 10,  Color = "#34D399" },
            new() { Slot = "D1", Label = "D1", Role = "dps",    X = -3, Z = -2, Color = "#F87171" },
            new() { Slot = "D2", Label = "D2", Role = "dps",    X = 3,  Z = -2, Color = "#F87171" },
            new() { Slot = "D3", Label = "D3", Role = "dps",    X = -3, Z = 2,  Color = "#FBBF24" },
            new() { Slot = "D4", Label = "D4", Role = "dps",    X = 3,  Z = 2,  Color = "#FBBF24" },
        };
        return m;
    }

    private static MechanicStrategy BuildDonutDodge()
    {
        var m = CreateBase("ドーナツ回避（中央集合）");
        m.Callout = "中央集合";
        m.WarningText = "中央安置";
        // Gimmick = null：AoeZones（ドーナツ）の RadiusM/InnerRadiusM が実寸として描かれる
        m.AoeZones = new List<StrategyAoeZone>
        {
            new()
            {
                Id = "donut_center",
                Label = "ドーナツ AoE",
                Shape = "donut",
                X = 0,
                Z = 0,
                RadiusM = 15.0,
                InnerRadiusM = 4.0,
                IsDanger = true,
            },
        };
        // 全員中央集合
        m.SpreadPositions = new List<StrategyPosition>
        {
            new() { Slot = "MT", Label = "MT", Role = "tank",   X = 0, Z = 0, Color = "#60A5FA" },
            new() { Slot = "ST", Label = "ST", Role = "tank",   X = 0, Z = 0, Color = "#60A5FA" },
            new() { Slot = "H1", Label = "H1", Role = "healer", X = 0, Z = 0, Color = "#34D399" },
            new() { Slot = "H2", Label = "H2", Role = "healer", X = 0, Z = 0, Color = "#34D399" },
            new() { Slot = "D1", Label = "D1", Role = "dps",    X = 0, Z = 0, Color = "#F87171" },
            new() { Slot = "D2", Label = "D2", Role = "dps",    X = 0, Z = 0, Color = "#F87171" },
            new() { Slot = "D3", Label = "D3", Role = "dps",    X = 0, Z = 0, Color = "#FBBF24" },
            new() { Slot = "D4", Label = "D4", Role = "dps",    X = 0, Z = 0, Color = "#FBBF24" },
        };
        return m;
    }

    private static MechanicStrategy BuildInnerCircleDodge()
    {
        var m = CreateBase("中央 AoE 回避（外周散開）");
        m.Callout = "外周散開";
        m.WarningText = "中央回避";
        // Gimmick = null：旧 "inner_circle" は半径 55% のハードコード描画で実寸と乖離する。
        // AoeZones に明示した RadiusM=8m を user_layout 経路で正確に描く。
        m.AoeZones = new List<StrategyAoeZone>
        {
            new()
            {
                Id = "inner_circle",
                Label = "中央 AoE",
                Shape = "circle",
                X = 0,
                Z = 0,
                RadiusM = 8.0,
                IsDanger = true,
            },
        };
        m.SpreadPositions = EightWayPositions();
        return m;
    }

    private static MechanicStrategy BuildTwoSideCleave()
    {
        var m = CreateBase("両翼攻撃回避（前後安置）");
        m.Callout = "前後安置";
        m.WarningText = "前後に分かれる";
        // Gimmick = null：扇 AoE 2 枚が AoeZones として正確な角度で描かれる
        // ボスの左右に扇 AoE。FFXIV 慣習：rotation_deg は 0=東 / 90=南 / 180=西 / -90=北。
        m.AoeZones = new List<StrategyAoeZone>
        {
            new()
            {
                Id = "left_cleave",
                Label = "左 cleave",
                Shape = "cone",
                X = 0,
                Z = 0,
                RadiusM = 20.0,
                RotationDeg = 180.0, // 西
                FanDeg = 90.0,
                IsDanger = true,
            },
            new()
            {
                Id = "right_cleave",
                Label = "右 cleave",
                Shape = "cone",
                X = 0,
                Z = 0,
                RadiusM = 20.0,
                RotationDeg = 0.0, // 東
                FanDeg = 90.0,
                IsDanger = true,
            },
        };
        // 北側グループ・南側グループに分割
        m.SpreadPositions = new List<StrategyPosition>
        {
            new() { Slot = "MT", Label = "MT", Role = "tank",   X = -3, Z = -10, Color = "#60A5FA" },
            new() { Slot = "ST", Label = "ST", Role = "tank",   X = 3,  Z = -10, Color = "#60A5FA" },
            new() { Slot = "H1", Label = "H1", Role = "healer", X = -3, Z = 10,  Color = "#34D399" },
            new() { Slot = "H2", Label = "H2", Role = "healer", X = 3,  Z = 10,  Color = "#34D399" },
            new() { Slot = "D1", Label = "D1", Role = "dps",    X = -6, Z = -10, Color = "#F87171" },
            new() { Slot = "D2", Label = "D2", Role = "dps",    X = 6,  Z = -10, Color = "#F87171" },
            new() { Slot = "D3", Label = "D3", Role = "dps",    X = -6, Z = 10,  Color = "#FBBF24" },
            new() { Slot = "D4", Label = "D4", Role = "dps",    X = 6,  Z = 10,  Color = "#FBBF24" },
        };
        return m;
    }

    private static MechanicStrategy BuildTowerAssignments()
    {
        var m = CreateBase("タワー処理（4 タワー想定）");
        m.Callout = "タワー踏み";
        m.WarningText = "担当タワーへ";
        // Gimmick = null：オブジェクトマーカー（タワー位置）と散開ポジで表現
        // N=北 (Z=-) / E=東 (X=+) / S=南 (Z=+) / W=西 (X=-)
        m.ObjectMarkers = new List<StrategyObjectMarker>
        {
            new() { Id = "tower_n", Label = "N タワー", X = 0,   Z = -12, Color = "#FBBF24", Shape = "circle", Note = "MT + D1" },
            new() { Id = "tower_e", Label = "E タワー", X = 12,  Z = 0,   Color = "#FBBF24", Shape = "circle", Note = "ST + D2" },
            new() { Id = "tower_s", Label = "S タワー", X = 0,   Z = 12,  Color = "#FBBF24", Shape = "circle", Note = "H1 + D3" },
            new() { Id = "tower_w", Label = "W タワー", X = -12, Z = 0,   Color = "#FBBF24", Shape = "circle", Note = "H2 + D4" },
        };
        // ペアごとに 4 タワーへ
        m.SpreadPositions = new List<StrategyPosition>
        {
            new() { Slot = "MT", Label = "MT", Role = "tank",   X = -1, Z = -12, Color = "#60A5FA" },
            new() { Slot = "D1", Label = "D1", Role = "dps",    X = 1,  Z = -12, Color = "#F87171" },
            new() { Slot = "ST", Label = "ST", Role = "tank",   X = 12, Z = -1,  Color = "#60A5FA" },
            new() { Slot = "D2", Label = "D2", Role = "dps",    X = 12, Z = 1,   Color = "#F87171" },
            new() { Slot = "H1", Label = "H1", Role = "healer", X = -1, Z = 12,  Color = "#34D399" },
            new() { Slot = "D3", Label = "D3", Role = "dps",    X = 1,  Z = 12,  Color = "#FBBF24" },
            new() { Slot = "H2", Label = "H2", Role = "healer", X = -12, Z = -1, Color = "#34D399" },
            new() { Slot = "D4", Label = "D4", Role = "dps",    X = -12, Z = 1,  Color = "#FBBF24" },
        };
        return m;
    }

    private static MechanicStrategy BuildTankSwap()
    {
        var m = CreateBase("タンクスワップ");
        m.Callout = "スイッチ";
        m.WarningText = "スイッチ準備";
        // Gimmick = null：タンクスワップは TTS と DisableMinimap で十分（位置移動なし）
        m.DisableMinimap = true;
        m.Role = "tank";
        return m;
    }
}
