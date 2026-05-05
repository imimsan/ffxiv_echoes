using System.Collections.Generic;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FfxivEchoes.Capture;
using FfxivEchoes.Commands;
using FfxivEchoes.Commands.Handlers;
using FfxivEchoes.Diagnostics;
using FfxivEchoes.Events;
using FfxivEchoes.Windows;
using FfxivEchoes.Windows.Tabs;

namespace FfxivEchoes;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;

    public Configuration Configuration { get; }
    public WindowSystem WindowSystem { get; } = new("FfxivEchoes");

    private readonly MainWindow _mainWindow;
    private readonly CommandRouter _commandRouter;

    // ── M3: イベントキャプチャ層 ─────────────────────────
    private readonly IEventBus _eventBus;
    private readonly CombatClock _combatClock;
    private readonly CombatStateCapture _combatStateCapture;
    private readonly ZoneCapture _zoneCapture;
    private readonly CastCapture _castCapture;
    private readonly StatusCapture _statusCapture;
    private readonly HpCapture _hpCapture;
    private readonly DebugChatEcho _debugChatEcho;

    public Plugin()
    {
        Configuration = Configuration.LoadAndMigrate(PluginInterface, Log);

        // タブ／ウィンドウ
        _mainWindow = new MainWindow(BuildTabs());
        WindowSystem.AddWindow(_mainWindow);

        // コマンドルータ
        _commandRouter = BuildCommandRouter();
        CommandManager.AddHandler(CommandRouter.RootCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "FFXIV Echoes：絶コンテンツ攻略支援。 "
                          + $"{CommandRouter.RootCommand} help でサブコマンド一覧。",
        });

        // M3: イベントバス + キャプチャ群
        _eventBus = new InMemoryEventBus(Log);
        _combatClock = new CombatClock(_eventBus);
        _combatStateCapture = new CombatStateCapture(Condition, _eventBus, Log);
        _zoneCapture = new ZoneCapture(ClientState, DataManager, _eventBus, Log);
        _castCapture = new CastCapture(Framework, ObjectTable, DataManager, _eventBus, Log);
        _statusCapture = new StatusCapture(Framework, ObjectTable, DataManager, _eventBus, Log);
        _hpCapture = new HpCapture(Framework, ObjectTable, _eventBus, Log);
        _debugChatEcho = new DebugChatEcho(_eventBus, Configuration, ChatGui, _combatClock);

        // UI ビルダーへのフック
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += OpenSettings;
        PluginInterface.UiBuilder.OpenMainUi += OpenSettings;

        Log.Information("[FfxivEchoes] Loaded v{Version} (config v{ConfigVersion}), capture services online",
            PluginInterface.Manifest.AssemblyVersion, Configuration.Version);
    }

    public void Dispose()
    {
        Log.Information("[FfxivEchoes] Unloading…");

        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenSettings;
        PluginInterface.UiBuilder.OpenMainUi -= OpenSettings;

        // キャプチャ群を逆順で破棄（DebugEcho が他のキャプチャに依存していないが念のため）
        _debugChatEcho.Dispose();
        _hpCapture.Dispose();
        _statusCapture.Dispose();
        _castCapture.Dispose();
        _zoneCapture.Dispose();
        _combatStateCapture.Dispose();
        _combatClock.Dispose();

        WindowSystem.RemoveAllWindows();
        _mainWindow.Dispose();

        CommandManager.RemoveHandler(CommandRouter.RootCommand);
    }

    private void OnCommand(string command, string args) => _commandRouter.Dispatch(args);

    /// <summary>SPEC.md §13.3「<c>/myplugin → 設定画面を開く</c>」に対応するエントリ。</summary>
    private void OpenSettings() => _mainWindow.Open(MainWindow.DefaultTabId);

    private List<ITab> BuildTabs() => new()
    {
        new ContentListTab(),
        new TriggerEditorTab(),
        new LiveHudTab(),
        new AudioTab(Configuration),
        new ProfileTab(),
        new ImportExportTab(),
        new GeneralSettingsTab(Configuration, PluginInterface),
    };

    private CommandRouter BuildCommandRouter()
    {
        var router = new CommandRouter(OpenSettings, Log, ChatGui);

        router.Register(new DebugCommand(Configuration, ChatGui));
        router.Register(new PendingCommand(
            verb: "record",
            usage: "record <on|off|auto>",
            description: "戦闘ログ記録モードの制御",
            plannedFor: "M4",
            chatGui: ChatGui));
        router.Register(new PendingCommand(
            verb: "reload",
            usage: "reload",
            description: "トリガー定義の再読み込み",
            plannedFor: "M5",
            chatGui: ChatGui));
        router.Register(new PendingCommand(
            verb: "timeline",
            usage: "timeline <all|configured|toggle|hide>",
            description: "ライブHUDタイムラインの表示制御",
            plannedFor: "F3",
            chatGui: ChatGui));
        router.Register(new PendingCommand(
            verb: "profile",
            usage: "profile <name>",
            description: "アクティブプロファイルの切替",
            plannedFor: "F10",
            chatGui: ChatGui));
        router.Register(new HelpCommand(router, ChatGui));

        return router;
    }
}
