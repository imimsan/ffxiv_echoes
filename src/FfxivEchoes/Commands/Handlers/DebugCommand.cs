using Dalamud.Plugin.Services;

namespace FfxivEchoes.Commands.Handlers;

public sealed class DebugCommand : ICommandHandler
{
    public string Verb => "debug";
    public string Usage => "debug <on|off|toggle>";
    public string Description => "デバッグモードの ON/OFF を切り替えます";

    private readonly Configuration _configuration;
    private readonly IChatGui _chatGui;

    public DebugCommand(Configuration configuration, IChatGui chatGui)
    {
        _configuration = configuration;
        _chatGui = chatGui;
    }

    public void Execute(string args)
    {
        var token = args.Trim().ToLowerInvariant();

        bool? next = token switch
        {
            "on" or "true" or "1" or "enable" => true,
            "off" or "false" or "0" or "disable" => false,
            "toggle" or "" => !_configuration.DebugMode,
            _ => null,
        };

        if (next is null)
        {
            _chatGui.Print($"[FFXIV Echoes] 使い方: {CommandRouter.RootCommand} {Usage}");
            return;
        }

        _configuration.DebugMode = next.Value;
        _configuration.Save();
        _chatGui.Print($"[FFXIV Echoes] デバッグモード: {(next.Value ? "ON" : "OFF")}");
    }
}
