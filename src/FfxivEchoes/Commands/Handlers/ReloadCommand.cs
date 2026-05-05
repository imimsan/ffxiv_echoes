using Dalamud.Plugin.Services;
using FfxivEchoes.Triggers;

namespace FfxivEchoes.Commands.Handlers;

/// <summary>
/// <c>/echoes reload</c>。トリガー定義をディスクから再読み込みする。
/// </summary>
public sealed class ReloadCommand : ICommandHandler
{
    public string Verb => "reload";
    public string Usage => "reload";
    public string Description => "トリガー定義をファイルから再読み込み";

    private readonly TriggerStore _store;
    private readonly IChatGui _chatGui;

    public ReloadCommand(TriggerStore store, IChatGui chatGui)
    {
        _store = store;
        _chatGui = chatGui;
    }

    public void Execute(string args)
    {
        _store.Reload();
        var loaded = _store.LoadedFileCount;
        var errors = _store.LastErrorCount;
        if (errors > 0)
        {
            _chatGui.PrintError(
                $"[FFXIV Echoes] トリガー再読込：成功 {loaded} 件、エラー {errors} 件。/xllog で詳細確認してください。");
        }
        else
        {
            _chatGui.Print($"[FFXIV Echoes] トリガー再読込完了：{loaded} 件のコンテンツを読み込みました。");
        }
    }
}
