using System.Collections.Generic;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FfxivEchoes.Actions;
using FfxivEchoes.Actions.Handlers;
using FfxivEchoes.Capture;
using FfxivEchoes.Commands;
using FfxivEchoes.Commands.Handlers;
using FfxivEchoes.Diagnostics;
using FfxivEchoes.Events;
using FfxivEchoes.Profiles;
using FfxivEchoes.Recording;
using FfxivEchoes.SafeZone;
using FfxivEchoes.SafeZone.Presets;
using FfxivEchoes.Triggers;
using FfxivEchoes.Triggers.Matching;
using FfxivEchoes.Variables;
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
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;

    public Configuration Configuration { get; }
    public WindowSystem WindowSystem { get; } = new("FfxivEchoes");

    private readonly MainWindow _mainWindow;
    private readonly CommandRouter _commandRouter;
    private readonly TabContext _tabContext = new();
    private readonly RecordingScanner _recordingScanner;

    // ── M3: イベントキャプチャ層 ─────────────────────────
    private readonly IEventBus _eventBus;
    private readonly CombatClock _combatClock;
    private readonly CombatStateCapture _combatStateCapture;
    private readonly ZoneCapture _zoneCapture;
    private readonly CastCapture _castCapture;
    private readonly StatusCapture _statusCapture;
    private readonly HpCapture _hpCapture;
    private readonly ObjectCapture _objectCapture;
    private readonly DebugChatEcho _debugChatEcho;

    // ── M4: ロガー（録画） ────────────────────────────────
    private readonly RecordingController _recordingController;
    private readonly BattleRecorder _battleRecorder;

    // ── M5: トリガー定義 ─────────────────────────────────
    private readonly TriggerLoader _triggerLoader;
    private readonly TriggerStore _triggerStore;
    private readonly TriggerWatcher _triggerWatcher;

    // ── M10: バックアップ ────────────────────────────────
    private readonly TriggerBackupManager _backupManager;

    // ── F10: プロファイル ────────────────────────────────
    private readonly ProfileStore _profileStore;

    // ── F11: インポート/エクスポート ──────────────────────
    private readonly TriggerExportImport _triggerExportImport;

    // ── P1+P2: 状態変数 ──────────────────────────────────
    private readonly VariableStore _variableStore;

    // ── M6 + F2: トリガーエンジン ────────────────────────
    private readonly TargetResolver _targetResolver;
    private readonly EventMatcher _eventMatcher;
    private readonly ConditionEvaluator _conditionEvaluator;
    private readonly TriggerEngine _triggerEngine;

    // ── M7: アクションディスパッチャ ──────────────────────
    private readonly OverlayWindow _overlayWindow;
    private readonly TtsHandler _ttsHandler;
    private readonly ActionDispatcher _actionDispatcher;

    // ── F3: ライブHUD ────────────────────────────────────
    private readonly LiveTimelineWindow _liveTimelineWindow;

    // ── F4-F7: 安置計算プリセット ─────────────────────────
    private readonly SafeZoneEngine _safeZoneEngine;
    private readonly SafeZoneContextBuilder _safeZoneContextBuilder;

    // ── F8: 位置情報出力 ─────────────────────────────────
    private readonly WorldOverlayWindow _worldOverlayWindow;

    // ── M9: オーディオデバイス ────────────────────────────
    private readonly AudioDeviceEnumerator _audioDevices;

    public Plugin()
    {
        Configuration = Configuration.LoadAndMigrate(PluginInterface, Log);

        // M5 + M10: トリガー定義 + バックアップ
        _backupManager = new TriggerBackupManager(PluginInterface.ConfigDirectory.FullName, Log);
        _triggerLoader = new TriggerLoader(PluginInterface.ConfigDirectory.FullName, Log);
        _triggerStore = new TriggerStore(_triggerLoader, Log, _backupManager);
        _triggerStore.Reload();
        _triggerWatcher = new TriggerWatcher(_triggerLoader.TriggersDirectory, _triggerStore, Log);

        // F10: プロファイル
        _profileStore = new ProfileStore(PluginInterface.ConfigDirectory.FullName, Log);
        _profileStore.Reload();

        // F11: インポート/エクスポート
        _triggerExportImport = new TriggerExportImport(PluginInterface, _triggerStore, Log);

        // M8: 録画スキャナ
        _recordingScanner = new RecordingScanner(PluginInterface, Log);

        // M9: オーディオデバイス（タブが参照するので先）
        _audioDevices = new AudioDeviceEnumerator(Log);

        // M3: イベントバスを早期構築（タブのプレビュー機能が参照するため）
        _eventBus = new InMemoryEventBus(Log);

        // P1: 状態変数（CombatEnded で自動リセット）
        _variableStore = new VariableStore(_eventBus, Log);

        // タブ／ウィンドウ
        _mainWindow = new MainWindow(BuildTabs(), _tabContext);
        WindowSystem.AddWindow(_mainWindow);

        _overlayWindow = new OverlayWindow();
        WindowSystem.AddWindow(_overlayWindow);

        // コマンドルータ
        _commandRouter = BuildCommandRouter();
        CommandManager.AddHandler(CommandRouter.RootCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "FFXIV Echoes：絶コンテンツ攻略支援。 "
                          + $"{CommandRouter.RootCommand} help でサブコマンド一覧。",
        });

        // M3: キャプチャ群（イベントバスは前段で構築済み）
        _combatClock = new CombatClock(_eventBus);
        _combatStateCapture = new CombatStateCapture(Condition, _eventBus, Log);
        _zoneCapture = new ZoneCapture(ClientState, DataManager, _eventBus, Log);
        _castCapture = new CastCapture(Framework, ObjectTable, DataManager, _eventBus, Log);
        _statusCapture = new StatusCapture(Framework, ObjectTable, DataManager, _eventBus, Log);
        _hpCapture = new HpCapture(Framework, ObjectTable, _eventBus, Log);
        _objectCapture = new ObjectCapture(Framework, ObjectTable, _eventBus, Log);
        _debugChatEcho = new DebugChatEcho(_eventBus, Configuration, ChatGui, _combatClock);

        // M4: ロガー
        _recordingController = new RecordingController(Log, _triggerStore);
        _battleRecorder = new BattleRecorder(
            _eventBus, _recordingController, PluginInterface,
            PartyList, ClientState, ObjectTable, PlayerState, DataManager, Log);

        // M6 + F2: トリガーエンジン（複合条件評価器付き）
        _targetResolver = new TargetResolver(PartyList, PlayerState);
        _eventMatcher = new EventMatcher(_targetResolver);
        _conditionEvaluator = new ConditionEvaluator(_targetResolver, _variableStore);
        _triggerEngine = new TriggerEngine(_eventBus, _triggerStore, _eventMatcher, _conditionEvaluator, Log,
            activeProfileGetter: () => _profileStore.Get(Configuration.ActiveProfile),
            variables: _variableStore);

        // F3: ライブタイムライン HUD
        _liveTimelineWindow = new LiveTimelineWindow(_eventBus, _combatClock, _triggerStore);
        WindowSystem.AddWindow(_liveTimelineWindow);

        // F4-F7: 安置計算プリセット（合計 15 種）
        SafeZoneEngine? engineRef = null;
        var intersectionPreset = new IntersectionPreset((c, ctx) => engineRef!.Calculate(c, ctx));
        var safeZonePresets = new ISafeZonePreset[]
        {
            // F4: 基本 6 種
            new FixedPreset(),
            new BossRelativePreset(),
            new MarkerRelativePreset(Log),
            new ArenaCenterRelativePreset(),
            new InverseOfTelegraphPreset(),
            new PartyMemberRelativePreset(),
            // F5: オブジェクト検知 4 種
            new FindActorWithStatusPreset(ObjectTable),
            new FindActorWithoutStatusPreset(ObjectTable),
            new FindActorNotCastingPreset(ObjectTable),
            new FindActorByDistancePreset(ObjectTable),
            // F6: 関係性 3 種
            new MidpointPreset(ObjectTable),
            new BetweenActorsPreset(ObjectTable),
            new LinePerpendicularPreset(ObjectTable),
            // F7: 高度 2 種（telegraph_gap + intersection）
            new TelegraphGapPreset(),
            intersectionPreset,
        };
        _safeZoneEngine = new SafeZoneEngine(safeZonePresets, Log);
        engineRef = _safeZoneEngine; // IntersectionPreset の遅延参照を解決
        _safeZoneContextBuilder = new SafeZoneContextBuilder(ObjectTable, PartyList);

        // F8: ワールドオーバーレイ
        _worldOverlayWindow = new WorldOverlayWindow(GameGui, ObjectTable);
        WindowSystem.AddWindow(_worldOverlayWindow);

        // M7 + F8: アクションディスパッチャ
        _ttsHandler = new TtsHandler(Configuration, Log);
        var wavHandler = new WavHandler(Configuration, PluginInterface, _audioDevices, Log);
        var handlers = new IActionHandler[]
        {
            _ttsHandler,
            wavHandler,
            new ChatEchoHandler(ChatGui),
            new OverlayTextHandler(_overlayWindow),
            new TimerBarHandler(_overlayWindow),
            // F8: 位置情報出力
            new DirectionCallHandler(_safeZoneEngine, _safeZoneContextBuilder,
                Configuration, _overlayWindow, _ttsHandler, ChatGui, Log),
            new ScreenArrowHandler(_safeZoneEngine, _safeZoneContextBuilder, _worldOverlayWindow, Log),
            new FieldMarkerHandler(_safeZoneEngine, _safeZoneContextBuilder, _worldOverlayWindow, Log),
            new ProximityFeedbackHandler(_safeZoneEngine, _safeZoneContextBuilder, wavHandler, ChatGui, Log),
        };
        _actionDispatcher = new ActionDispatcher(_eventBus, handlers, Log);

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

        // アクションディスパッチャを先に止めて新規アクション実行を遮断
        _actionDispatcher.Dispose();
        _ttsHandler.Dispose();

        // トリガーエンジンを止めて新規 TriggerFired を発生させない
        _triggerEngine.Dispose();
        _variableStore.Dispose();

        // ロガーを閉じて録画ファイルを確実にフラッシュ
        _battleRecorder.Dispose();

        // トリガー監視・ストアの停止
        _triggerWatcher.Dispose();

        // キャプチャ群を逆順で破棄（DebugEcho が他のキャプチャに依存していないが念のため）
        _debugChatEcho.Dispose();
        _objectCapture.Dispose();
        _hpCapture.Dispose();
        _statusCapture.Dispose();
        _castCapture.Dispose();
        _zoneCapture.Dispose();
        _combatStateCapture.Dispose();
        _combatClock.Dispose();

        WindowSystem.RemoveAllWindows();
        _mainWindow.Dispose();
        _overlayWindow.Dispose();
        _liveTimelineWindow.Dispose();
        _worldOverlayWindow.Dispose();

        CommandManager.RemoveHandler(CommandRouter.RootCommand);
    }

    private void OnCommand(string command, string args) => _commandRouter.Dispatch(args);

    /// <summary>SPEC.md §13.3「<c>/myplugin → 設定画面を開く</c>」に対応するエントリ。</summary>
    private void OpenSettings() => _mainWindow.Open(MainWindow.DefaultTabId);

    private List<ITab> BuildTabs() => new()
    {
        new ContentListTab(_triggerStore, _recordingScanner, _tabContext),
        new TriggerEditorTab(_triggerStore, _recordingScanner, _tabContext, _eventBus),
        new LiveHudTab(),
        new AudioTab(Configuration, _audioDevices),
        new ProfileTab(_profileStore, _triggerStore, Configuration),
        new ImportExportTab(_triggerStore, _triggerExportImport),
        new GeneralSettingsTab(Configuration, PluginInterface),
    };

    private CommandRouter BuildCommandRouter()
    {
        var router = new CommandRouter(OpenSettings, Log, ChatGui);

        router.Register(new DebugCommand(Configuration, ChatGui));
        router.Register(new RecordCommand(_recordingController, _battleRecorder, ChatGui));
        router.Register(new ReloadCommand(_triggerStore, ChatGui));
        router.Register(new TimelineCommand(_liveTimelineWindow, ChatGui));
        router.Register(new ProfileCommand(_profileStore, Configuration, ChatGui));
        router.Register(new HelpCommand(router, ChatGui));

        return router;
    }
}
