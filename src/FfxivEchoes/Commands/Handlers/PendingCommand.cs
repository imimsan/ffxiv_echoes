using Dalamud.Plugin.Services;

namespace FfxivEchoes.Commands.Handlers;

/// <summary>
/// 後続マイルストーンで実装予定のコマンドの「占位ハンドラ」。
/// 呼び出されたら未実装である旨と実装予定マイルストーンをチャットに通知する。
/// </summary>
public sealed class PendingCommand : ICommandHandler
{
    public string Verb { get; }
    public string Usage { get; }
    public string Description { get; }

    private readonly string _plannedFor;
    private readonly IChatGui _chatGui;

    public PendingCommand(string verb, string usage, string description, string plannedFor, IChatGui chatGui)
    {
        Verb = verb;
        Usage = usage;
        Description = description + $"（{plannedFor} で実装予定）";
        _plannedFor = plannedFor;
        _chatGui = chatGui;
    }

    public void Execute(string args)
    {
        _chatGui.Print(
            $"[FFXIV Echoes] '{Verb}' は {_plannedFor} で実装予定のコマンドです。" +
            "ロードマップは roadmap.md を参照。");
    }
}
