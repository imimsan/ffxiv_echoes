using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;

namespace FfxivEchoes.Commands;

public sealed class CommandRouter
{
    public const string RootCommand = "/echoes";

    private readonly Dictionary<string, ICommandHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action _onNoArgs;
    private readonly IPluginLog _log;
    private readonly IChatGui _chatGui;

    public CommandRouter(Action onNoArgs, IPluginLog log, IChatGui chatGui)
    {
        _onNoArgs = onNoArgs;
        _log = log;
        _chatGui = chatGui;
    }

    public void Register(ICommandHandler handler)
    {
        _handlers[handler.Verb] = handler;
    }

    public IReadOnlyDictionary<string, ICommandHandler> Handlers => _handlers;

    public void Dispatch(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            _onNoArgs();
            return;
        }

        var trimmed = args.Trim();
        var spaceIndex = trimmed.IndexOf(' ');
        var verb = spaceIndex < 0 ? trimmed : trimmed[..spaceIndex];
        var rest = spaceIndex < 0 ? string.Empty : trimmed[(spaceIndex + 1)..].TrimStart();

        if (!_handlers.TryGetValue(verb, out var handler))
        {
            _chatGui.PrintError(
                $"[FFXIV Echoes] 不明なコマンド: '{verb}'。{RootCommand} help でコマンド一覧を確認できます。");
            return;
        }

        try
        {
            handler.Execute(rest);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[FfxivEchoes] コマンド '{Verb}' の実行中に例外が発生", verb);
            _chatGui.PrintError($"[FFXIV Echoes] コマンド実行エラー: {ex.Message}");
        }
    }
}
