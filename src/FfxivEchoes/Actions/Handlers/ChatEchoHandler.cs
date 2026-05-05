using Dalamud.Plugin.Services;
using FfxivEchoes.Events;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Actions.Handlers;

/// <summary>
/// 自分のチャットウィンドウにテキスト出力（SPEC.md §5.1 chat_echo）。
/// </summary>
public sealed class ChatEchoHandler : IActionHandler
{
    public string Type => "chat_echo";

    private readonly IChatGui _chatGui;

    public ChatEchoHandler(IChatGui chatGui)
    {
        _chatGui = chatGui;
    }

    public void Execute(ActionDefinition action, TriggerFiredEvent context)
    {
        if (string.IsNullOrEmpty(action.Text))
        {
            return;
        }
        _chatGui.Print($"[Echoes] {action.Text}");
    }
}
