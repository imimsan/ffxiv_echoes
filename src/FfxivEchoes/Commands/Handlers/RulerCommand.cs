using System;

namespace FfxivEchoes.Commands.Handlers;

/// <summary>
/// <c>/echoes ruler</c>。アリーナ寸法計測ウィンドウを開閉する。
/// </summary>
/// <remarks>
/// 練習部屋でアリーナの実寸が分からないときに使う。プレイヤー位置 / ウェイマーク位置 /
/// 距離をライブ表示し、自分中心の同心円も世界空間に重ねる。
/// </remarks>
public sealed class RulerCommand : ICommandHandler
{
    public string Verb => "ruler";
    public string Usage => "ruler";
    public string Description => "アリーナ寸法計測ウィンドウを開閉（練習部屋で実寸を測る用）";

    private readonly Action _toggle;

    public RulerCommand(Action toggle)
    {
        _toggle = toggle;
    }

    public void Execute(string args)
    {
        _toggle();
    }
}
