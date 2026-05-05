using System.Linq;
using Dalamud.Plugin.Services;

namespace FfxivEchoes.Commands.Handlers;

public sealed class HelpCommand : ICommandHandler
{
    public string Verb => "help";
    public string Usage => "help";
    public string Description => "コマンド一覧を表示します";

    private readonly CommandRouter _router;
    private readonly IChatGui _chatGui;

    public HelpCommand(CommandRouter router, IChatGui chatGui)
    {
        _router = router;
        _chatGui = chatGui;
    }

    public void Execute(string args)
    {
        _chatGui.Print($"[FFXIV Echoes] コマンド一覧:");
        _chatGui.Print($"  {CommandRouter.RootCommand,-32} : 設定画面を開く");

        foreach (var handler in _router.Handlers.Values.OrderBy(h => h.Verb))
        {
            var usage = $"{CommandRouter.RootCommand} {handler.Usage}";
            _chatGui.Print($"  {usage,-32} : {handler.Description}");
        }
    }
}
