using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FfxivEchoes.Windows;

namespace FfxivEchoes.Commands.Handlers;

/// <summary>
/// ミニマップ / 床塗りの AoE 描画パイプラインが動作しているかを手動で確認する
/// デバッグコマンド。`/echoes test-aoe [donut|circle|cone|rect]` でプレイヤー位置を
/// 中心に半径 10m のテスト AoE を 10 秒間描画する。
/// 「自動描画が出ない / 出すぎる」と疑ったとき、描画パイプ自体が動いているかを
/// 切り分けるためのもの。
/// </summary>
public sealed class TestAoeCommand : ICommandHandler
{
    public string Verb => "test-aoe";
    public string Usage => "test-aoe [donut|circle|cone|rect]";
    public string Description => "プレイヤー位置にテスト AoE をミニマップ + 床塗りで 10 秒描画";

    private readonly MinimapWindow _minimap;
    private readonly IObjectTable _objectTable;
    private readonly IChatGui _chatGui;
    private readonly IPluginLog _log;

    public TestAoeCommand(
        MinimapWindow minimap,
        IObjectTable objectTable,
        IChatGui chatGui,
        IPluginLog log)
    {
        _minimap = minimap;
        _objectTable = objectTable;
        _chatGui = chatGui;
        _log = log;
    }

    public void Execute(string args)
    {
        var shape = args.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(shape)) shape = "donut";

        var player = _objectTable.LocalPlayer;
        if (player is null)
        {
            _chatGui.PrintError("[FFXIV Echoes] テスト AoE: ローカルプレイヤーが取得できません");
            return;
        }

        var pos = new Vector3(player.Position.X, player.Position.Y, player.Position.Z);

        // Lumina CastType マッピング：2=Circle、3=Cone、4=Rect、6=Donut
        var (gimmick, castType, radius, halfWidth) = shape switch
        {
            "donut" => ("outer_ring", (int?)6, 10f, (float?)null),
            "circle" => ("inner_circle", (int?)2, 10f, (float?)null),
            "cone" => ("cone", (int?)3, 10f, (float?)null),
            "rect" => ("cone", (int?)4, 10f, (float?)2.5f),
            _ => ("outer_ring", (int?)6, 10f, (float?)null),
        };

        _minimap.AddArenaView(
            gimmick: gimmick,
            callout: $"テスト AoE ({shape})",
            durationSec: 10.0,
            direction: "N",
            fanDeg: 90.0,
            arenaRadius: 20.0,
            sourceWorld: pos,
            aoeRadius: radius,
            aoeHalfWidthM: halfWidth,
            aoeCastType: castType,
            arenaShape: "circle",
            lockedArenaCenter: pos);

        _chatGui.Print(
            $"[FFXIV Echoes] テスト AoE: shape={shape} radius={radius}m " +
            $"pos=({pos.X:F1},{pos.Z:F1}) duration=10s をミニマップに描画");
        _log.Information(
            "[FfxivEchoes] TestAoe: shape={Shape} radius={R}m pos=({X:F1},{Z:F1})",
            shape, radius, pos.X, pos.Z);
    }
}
