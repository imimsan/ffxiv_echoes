using Dalamud.Plugin.Services;
using FfxivEchoes.Windows;

namespace FfxivEchoes.Commands.Handlers;

/// <summary>
/// <c>/echoes timeline &lt;all|configured|hide|toggle&gt;</c>。
/// SPEC.md §8.4 のスラッシュコマンドに準拠。
/// </summary>
public sealed class TimelineCommand : ICommandHandler
{
    public string Verb => "timeline";
    public string Usage => "timeline <all|configured|hide|toggle>";
    public string Description => "ライブHUDタイムラインの表示モード切替";

    private readonly LiveTimelineWindow _window;
    private readonly IChatGui _chatGui;

    public TimelineCommand(LiveTimelineWindow window, IChatGui chatGui)
    {
        _window = window;
        _chatGui = chatGui;
    }

    public void Execute(string args)
    {
        var token = args.Trim().ToLowerInvariant();
        var mode = token switch
        {
            "all" => (LiveTimelineWindow.DisplayMode?)LiveTimelineWindow.DisplayMode.All,
            "configured" => LiveTimelineWindow.DisplayMode.Configured,
            "hide" or "off" => LiveTimelineWindow.DisplayMode.Hidden,
            "toggle" or "" => _window.Mode == LiveTimelineWindow.DisplayMode.Hidden
                ? LiveTimelineWindow.DisplayMode.Configured
                : LiveTimelineWindow.DisplayMode.Hidden,
            _ => null,
        };
        if (mode is null)
        {
            _chatGui.Print($"[FFXIV Echoes] 使い方: {CommandRouter.RootCommand} {Usage}");
            return;
        }
        _window.Mode = mode.Value;
        _chatGui.Print($"[FFXIV Echoes] ライブタイムライン: {mode.Value}");
    }
}
