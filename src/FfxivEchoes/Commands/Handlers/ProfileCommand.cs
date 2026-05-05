using System;
using Dalamud.Plugin.Services;
using FfxivEchoes.Profiles;

namespace FfxivEchoes.Commands.Handlers;

public sealed class ProfileCommand : ICommandHandler
{
    public string Verb => "profile";
    public string Usage => "profile [name]";
    public string Description => "アクティブプロファイルの切替（引数なしで一覧と現在値）";

    private readonly ProfileStore _store;
    private readonly Configuration _configuration;
    private readonly IChatGui _chatGui;

    public ProfileCommand(ProfileStore store, Configuration configuration, IChatGui chatGui)
    {
        _store = store;
        _configuration = configuration;
        _chatGui = chatGui;
    }

    public void Execute(string args)
    {
        var name = args.Trim();
        if (string.IsNullOrEmpty(name))
        {
            ListProfiles();
            return;
        }

        if (name.Equals("reload", StringComparison.OrdinalIgnoreCase))
        {
            _store.Reload();
            _chatGui.Print($"[FFXIV Echoes] プロファイル再読込完了：{_store.All.Count} 件");
            return;
        }

        var profile = _store.Get(name);
        if (profile is null)
        {
            _chatGui.PrintError($"[FFXIV Echoes] プロファイル '{name}' が見つかりません。一覧は {CommandRouter.RootCommand} {Verb}");
            return;
        }
        _configuration.ActiveProfile = name;
        _configuration.Save();
        _chatGui.Print($"[FFXIV Echoes] アクティブプロファイル: {profile.DisplayName} ({profile.Name})");
    }

    private void ListProfiles()
    {
        _chatGui.Print($"[FFXIV Echoes] プロファイル一覧（現在: {_configuration.ActiveProfile}）:");
        foreach (var (name, p) in _store.All)
        {
            var marker = string.Equals(name, _configuration.ActiveProfile, StringComparison.OrdinalIgnoreCase) ? "● " : "  ";
            _chatGui.Print($"  {marker}{p.Name,-16} {p.DisplayName}");
        }
    }
}
