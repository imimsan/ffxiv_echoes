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
using FfxivEchoes.Scripting;
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
    [PluginService] internal static IGameInteropProvider HookProvider { get; private set; } = null!;

    public Configuration Configuration { get; }
    public WindowSystem WindowSystem { get; } = new("FfxivEchoes");

    private readonly MainWindow _mainWindow;
    private readonly CommandRouter _commandRouter;
    private readonly TabContext _tabContext = new();
    private readonly RecordingScanner _recordingScanner;
    private readonly RecordingWarmupService _recordingWarmup;

    // ── M3: イベントキャプチャ層 ─────────────────────────
    private readonly IEventBus _eventBus;
    private readonly CombatClock _combatClock;
    private readonly CombatStateCapture _combatStateCapture;
    private readonly ZoneCapture _zoneCapture;
    private readonly CastCapture _castCapture;
    private readonly StatusCapture _statusCapture;
    private readonly HpCapture _hpCapture;
    private readonly ObjectCapture _objectCapture;
    private readonly PlayerPositionCapture _playerPositionCapture;
    private readonly ActionEffectCapture _actionEffectCapture;
    private readonly DebugChatEcho _debugChatEcho;

    // ── Splatoon 流：キャスト開始時の rotation スナップショット ──────
    private CastRotationSnapshot? _castRotationSnapshot;

    // ── Lumina ベースの PC / pet 判定（IsPlayer フラグ付与に使う共用サービス）──
    private LuminaPcDetector? _pcDetector;

    // ── Lumina Action sheet ルックアップ抽象。headless replay でモックに差し替え可能。──
    private LuminaActionLookup? _luminaActionLookup;

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

    // ── タイムラインノート（軽減等メモ） ──────────────────
    private NoteReminderService? _noteReminder;

    // ── 自動 AoE 描画 ──────────────────────────────────
    private AutoTelegraphService? _autoTelegraph;
    private AddObjectAoeService? _addObjectAoe;
    private AutoAttackTimingService? _autoAttackTiming;

    // ── Splatoon 流 actor 追跡型 AoE 床塗り（per-frame ライブ描画）──
    private ActorTrackedAoeService? _actorTrackedAoe;

    // ── 録画予測に基づく cast 予告（音声・オーバーレイ・床塗り）──────
    private PredictedCastReminderService? _predictedCastReminder;
    private PredictedObjectSpawnService? _predictedObjectSpawn;
    private PredictedObjectSpawnLearner? _predictedObjectSpawnLearner;

    // ── タイムライン予測 AoE の俯瞰図事前描画 ──────────────────────
    private UpcomingAoePreviewService? _upcomingAoePreview;

    // ── タイムライン分岐の判定サービス（パターン1 / パターン2 を観測で確定）────
    private BranchObserverService? _branchObserver;

    // ── 現在フェーズ追跡（前半 / 後半などのフェーズ境界を sync_point / hp_pct で確定）──
    private CurrentPhaseTracker? _phaseTracker;

    // ── メカニクス発動条件評価（cast / status / rotation 統合）──────
    private MechanicTriggerService? _mechanicTrigger;
    private AutoArenaCalibrationService? _autoArenaCalibration;

    // ── 同期オフセットトラッカー ──────────────────────────
    private SyncOffsetTracker? _syncOffset;

    // ── トリガー自動生成 ──────────────────────────────────
    private TriggerAutoGenerator? _autoGenerator;

    // ── 学習辞書 ──────────────────────────
    private SafeCallDictionary? _safeCallDictionary;

    // ── 全体攻撃検知（HP 相関で raid-wide マーク） ──────
    private RaidWideDetector? _raidWideDetector;

    // ── P1+P2: 状態変数 ──────────────────────────────────
    private readonly VariableStore _variableStore;

    // ── P5: カスタムスクリプト（スタブ） ──────────────────
    private readonly ScriptManager _scriptManager;

    // ── M6 + F2: トリガーエンジン ────────────────────────
    private readonly TargetResolver _targetResolver;
    private readonly EventMatcher _eventMatcher;
    private readonly ConditionEvaluator _conditionEvaluator;
    private readonly TriggerEngine _triggerEngine;

    // ── M7: アクションディスパッチャ ──────────────────────
    private readonly OverlayWindow _overlayWindow;
    private readonly MinimapWindow _minimapWindow;
    private readonly TtsHandler _ttsHandler;
    private readonly ActionDispatcher _actionDispatcher;

    // ── F3: ライブHUD（タイムライン）────────────────────
    // LiveTimelineWindow（横スクロール式）と CombatTimerHudWindow（戦闘時間カウント）は
    // ユーザー要望「不要」に基づき撤去済み。タイムラインは UpcomingEventsWindow（cactbot 風）に統合。
    private UpcomingEventsWindow? _upcomingWindow;

    // ── K3: デバフ HUD ────────────────────────────────────
    private DebuffHudWindow? _debuffHudWindow;

    // ── F4-F7: 安置計算プリセット ─────────────────────────
    private readonly SafeZoneEngine _safeZoneEngine;
    private readonly SafeZoneContextBuilder _safeZoneContextBuilder;

    // ── F8: 位置情報出力 ─────────────────────────────────
    private readonly WorldOverlayWindow _worldOverlayWindow;

    // ── 計測ツール（アリーナ寸法をユーザーに見せる） ────
    private readonly ArenaRulerWindow _arenaRulerWindow;

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

        // 録画キャッシュのウォームアップ：zone 入場時にバックグラウンドで集計しておき、
        // 戦闘開始フレームでの同期読み込み（開幕の固まり）を防ぐ。
        _recordingWarmup = new RecordingWarmupService(_eventBus, _recordingScanner, Log);

        // P1: 状態変数（CombatEnded で自動リセット）
        _variableStore = new VariableStore(_eventBus, Log);

        // ウィンドウ（MainWindow は依存先が揃った後で構築）
        _overlayWindow = new OverlayWindow();
        WindowSystem.AddWindow(_overlayWindow);

        // MinimapWindow は SafeZoneContextBuilder に依存するので後で構築する
        // MainWindow は LiveHudTab が _combatClock を使うので後で構築する
        // CommandRouter の構築は依存先（_recordingController 等）が揃った後に行う

        // M3: キャプチャ群（イベントバスは前段で構築済み）
        _combatClock = new CombatClock(_eventBus);
        _combatStateCapture = new CombatStateCapture(Condition, _eventBus, Log);
        _zoneCapture = new ZoneCapture(ClientState, DataManager, _eventBus, Log);
        // Lumina ベースの PC / pet 決定論判定。Capture 群に共通注入して
        // status / object_appear に IsPlayer フラグを付与し、BattleRecorder で録画を skip する。
        _pcDetector = new LuminaPcDetector(DataManager, ObjectTable, Log);

        _castCapture = new CastCapture(Framework, ObjectTable, DataManager, _eventBus, Log);
        _statusCapture = new StatusCapture(Framework, ObjectTable, DataManager, _eventBus, Log, _pcDetector);
        _hpCapture = new HpCapture(Framework, ObjectTable, _eventBus, Log, _pcDetector);
        _objectCapture = new ObjectCapture(Framework, ObjectTable, _eventBus, Log, _pcDetector);
        _playerPositionCapture = new PlayerPositionCapture(Framework, ObjectTable, _eventBus, Log);
        _actionEffectCapture = new ActionEffectCapture(HookProvider, ObjectTable, DataManager, _eventBus, Log);
        _debugChatEcho = new DebugChatEcho(_eventBus, Configuration, ChatGui, _combatClock);

        // Splatoon 流：cast 開始時の actor.Rotation を保持。
        // 後段の ActorTrackedAoeService が cone / rect AoE の向きとして参照する
        // （boss が cast 開始で向き lock するゲーム挙動に追従するための snapshot）。
        _castRotationSnapshot = new CastRotationSnapshot(_eventBus, ObjectTable, Log);

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

        // P5: カスタムスクリプト（スタブ。明示 LoadAll しない限り何もロードしない）
        var scriptContext = new ScriptContext(_eventBus, _triggerStore, _variableStore, Log);
        _scriptManager = new ScriptManager(PluginInterface, scriptContext, Log);

        // 同期オフセットトラッカー（録画予測 vs 実戦のズレを追跡）
        _syncOffset = new SyncOffsetTracker(_eventBus, _triggerStore, _combatClock, _recordingScanner, Log);

        // トリガー自動生成（録画 + Lumina から作る）
        _autoGenerator = new TriggerAutoGenerator(DataManager, Log);

        // 学習辞書 + 同時発火検知（AutoSafeCallPlanner 経由で参照される）
        _safeCallDictionary = new SafeCallDictionary(PluginInterface.ConfigDirectory.FullName, Log);
        // Initialize は null セット禁止 + 二重初期化警告。fail-fast で初期化漏れを起動時検出。
        AutoSafeCallPlanner.Initialize(_safeCallDictionary, Log);

        // タイムライン分岐の観測サービス：UpcomingEventsWindow がタイムライン構築時に
        // 参照するので、Window より前に構築する。Plugin 全体の他のサービスとは独立。
        _branchObserver = new BranchObserverService(_eventBus, _triggerStore, _combatClock, Log);

        // 現在フェーズ追跡：sync_point（phase 付き）通過 / hp_pct 跨ぎで PhaseTransitionedEvent を受け、
        // 過去フェーズのギミックをタイムライン / 読み上げから除外する判定を提供する。
        // 各リマインダー / ウィンドウより前に構築する。
        _phaseTracker = new CurrentPhaseTracker(_eventBus, _triggerStore, Log);

        // タイムラインノート：advance_warning_sec で先行通知。
        // branch_id が Rejected/Pending のメカニクス、過去フェーズのメカニクスは実通知からも外す。
        _noteReminder = new NoteReminderService(
            Framework, _eventBus, _triggerStore, _combatClock, PlayerState, _recordingScanner, _syncOffset, Log,
            _branchObserver.IsActiveOrCommon,
            _phaseTracker.IsPhaseActive);

        // 予測アドバンス警告は _worldOverlayWindow に依存するので、後段で構築する

        // 自動 AoE テレグラフ（auto_settings.show_auto_telegraphs で有効化、後段で _worldOverlayWindow 構築後に再設定）

        // 「次に来るイベント」HUD（cactbot 風の縮むバー縦積み形式）
        _upcomingWindow = new UpcomingEventsWindow(
            _eventBus, _combatClock, _triggerStore, _recordingScanner, _syncOffset,
            branchObserver: _branchObserver,
            phaseTracker: _phaseTracker);
        WindowSystem.AddWindow(_upcomingWindow);

        // K3: デバフ HUD（自分の弱体一覧）
        _debuffHudWindow = new DebuffHudWindow(_eventBus, ObjectTable, DataManager, Configuration);
        WindowSystem.AddWindow(_debuffHudWindow);

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
            // ノックバック着地点予測（KB+塔/落下対策。中点ではなく実投射先を出す）
            new KnockbackProjectionPreset(ObjectTable),
            // F7: 高度 2 種（telegraph_gap + intersection）
            new TelegraphGapPreset(),
            intersectionPreset,
            // P4: stored_position
            new StoredPositionPreset(_variableStore),
        };
        _safeZoneEngine = new SafeZoneEngine(safeZonePresets, Log);
        engineRef = _safeZoneEngine; // IntersectionPreset の遅延参照を解決
        _safeZoneContextBuilder = new SafeZoneContextBuilder(ObjectTable, PartyList);

        // ミニマップ（プレイヤー位置プロットに SafeZoneContextBuilder + 敵描画に ObjectTable 必要）
        _minimapWindow = new MinimapWindow(_safeZoneContextBuilder, ObjectTable);
        WindowSystem.AddWindow(_minimapWindow);

        // F8: ワールドオーバーレイ
        _worldOverlayWindow = new WorldOverlayWindow(GameGui, ObjectTable);
        WindowSystem.AddWindow(_worldOverlayWindow);

        // アリーナ寸法計測ツール：プレイヤー位置 / ウェイマーク座標 / 距離をライブ表示
        _arenaRulerWindow = new ArenaRulerWindow(ObjectTable, GameGui);
        WindowSystem.AddWindow(_arenaRulerWindow);

        // 自動 AoE 描画は 2 系統あり、性質が大きく違うので扱いも分ける：
        //
        //   AutoTelegraphService — 復活。
        //     ボスキャスト 1 件ごとに Lumina の EffectRange / OmenId を引いて
        //     **実寸の** AoE を描画する。学習不要で初見からそこそこ正確。
        //     全体攻撃マーク済キャストは `IsRaidWide` で skip、サイズがアリーナ 90% 超の
        //     巨大キャストも skip するので、画面が AoE 一面塗りになりにくい。
        //
        //   AddObjectAoeService — 学習済み AoE の object_appear 補完。
        //     半径が辞書または録画から取れる場合だけ、TriggerFiredEvent に変換して
        //     ミニマップと床描画の両方へ流す。未学習オブジェクトの既定円は出さない。
        // T3: AoeResolver / AutoTelegraphService が IDataManager 直参照から
        // IActionLookup 抽象化に切り替わったため、本番では LuminaActionLookup を渡す。
        _luminaActionLookup = new LuminaActionLookup(DataManager, Log);
        var actionLookup = _luminaActionLookup;
        _autoTelegraph = new AutoTelegraphService(
            _eventBus, actionLookup, ObjectTable, _worldOverlayWindow, _minimapWindow,
            _triggerStore, Log,
            branchActiveCheck: _branchObserver.IsActiveOrCommon);
        _addObjectAoe = new AddObjectAoeService(
            Framework, _eventBus, _minimapWindow, _triggerStore,
            _safeCallDictionary, _recordingScanner, actionLookup, ObjectTable, Log);

        // Splatoon 流ライブ描画サービス。AutoTelegraphService が「キャスト開始時に 1 回」
        // 床塗りしていたものを、これが「毎フレーム actor 位置・向きを再評価」する形で置換。
        // ボスがキャスト中にじわじわ動いても AoE が追従する。
        // Framework を渡すのは内包する AoeSequenceScheduler が Update を踏むため。
        _actorTrackedAoe = new ActorTrackedAoeService(
            _eventBus, Framework, DataManager, ObjectTable, _worldOverlayWindow,
            _castRotationSnapshot, _triggerStore, Log);

        // 録画予測 → 事前警告（TTS / オーバーレイ / ミニマップ / 床塗り）。
        // 予測時刻の advance_warning_sec 秒前に AoE を Predicted フェーズで先行表示し、
        // CastStartedEvent が来れば Confirmed 置き換え、来なければ自動消滅。
        // 「キャストが来てから着弾までしか表示されず避けられない」問題を構造的に解消。
        _predictedCastReminder = new PredictedCastReminderService(
            Framework, _eventBus, _triggerStore, _combatClock,
            _recordingScanner, _syncOffset, DataManager, ObjectTable,
            _worldOverlayWindow, _minimapWindow, Log,
            actorTracked: _actorTrackedAoe,
            branchActiveCheck: _branchObserver.IsActiveOrCommon,
            phaseActiveCheck: _phaseTracker.IsPhaseActive);

        // タイムライン予測 AoE を俯瞰図へ事前描画するサービス。
        // UpcomingEventsWindow が毎フレーム Publish し、MinimapWindow に反映する。
        _upcomingAoePreview = new UpcomingAoePreviewService(
            _eventBus, _triggerStore, _combatClock, DataManager, ObjectTable,
            _minimapWindow, Configuration, Log);
        _upcomingWindow!.AoePreview = _upcomingAoePreview;

        // 「Cast → N 秒後に Object 出現 → 即時 AoE」パターンを録画学習し、cast 検知時点で
        // 先取り予告を描画するサービス。月の底のパラデイグマ → ケツアクアトル 4 体のような
        // Dalamud ObjectTable 登録遅延が大きいギミックを ObjectTable を待たずに事前可視化。
        // docs/predicted-object-spawn-design.md 参照。
        _predictedObjectSpawnLearner = new PredictedObjectSpawnLearner(_recordingScanner, Log, DataManager);
        _predictedObjectSpawn = new PredictedObjectSpawnService(Framework, _eventBus, _triggerStore, Log);

        _autoAttackTiming = new AutoAttackTimingService(
            Framework, _eventBus, _triggerStore, ObjectTable, Log);

        _raidWideDetector = new RaidWideDetector(_eventBus, PartyList, PlayerState,
            _triggerStore, Log);

        // メカニクス発動条件サービス（cast / status_gain / status_lose / rotation 統合）
        _mechanicTrigger = new MechanicTriggerService(
            Framework, _eventBus, _triggerStore, ObjectTable, Log,
            _branchObserver.IsActiveOrCommon);

        // アリーナ寸法を戦闘中のプレイヤー位置から自動学習し、初回戦闘終了時にプロファイルへ反映。
        // 「ユーザーがルーラーで測る」「録画から推定ボタンを押す」を不要にするための自動化。
        _autoArenaCalibration = new AutoArenaCalibrationService(_eventBus, _triggerStore, Log);

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
            new ArenaViewHandler(_minimapWindow, _safeZoneEngine, _safeZoneContextBuilder, ObjectTable, Log,
                _triggerStore),
            // F8: 位置情報出力
            new DirectionCallHandler(_safeZoneEngine, _safeZoneContextBuilder,
                Configuration, _overlayWindow, _ttsHandler, ChatGui, Log),
            new ScreenArrowHandler(_safeZoneEngine, _safeZoneContextBuilder, _worldOverlayWindow, Log),
            new FieldMarkerHandler(_safeZoneEngine, _safeZoneContextBuilder, _worldOverlayWindow, Log),
            new ProximityFeedbackHandler(_safeZoneEngine, _safeZoneContextBuilder, wavHandler, ChatGui, Log),
            // P3: 連鎖トリガー
            new ChainTriggerHandler(_eventBus, _triggerStore, Log),
            // P4: 位置保存
            new StorePositionHandler(_safeZoneEngine, _safeZoneContextBuilder, _variableStore, Log),
        };
        _actionDispatcher = new ActionDispatcher(_eventBus, handlers, Configuration, Log, Framework, _triggerStore);

        // MainWindow（依存：BuildTabs 内で _combatClock / _eventBus を参照する LiveHudTab）
        _mainWindow = new MainWindow(BuildTabs(), _tabContext);
        WindowSystem.AddWindow(_mainWindow);

        // コマンドルータ：依存先（_recordingController / _battleRecorder / _liveTimelineWindow など）
        // が揃った後に構築する。先に構築すると null が捕まり、コマンド実行時に NRE になる
        _commandRouter = BuildCommandRouter();
        CommandManager.AddHandler(CommandRouter.RootCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "FFXIV Echoes：絶コンテンツ攻略支援。 "
                          + $"{CommandRouter.RootCommand} help でサブコマンド一覧。",
        });

        // UI ビルダーへのフック
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += OpenSettings;
        PluginInterface.UiBuilder.OpenMainUi += OpenSettings;

        // すべての subscriber が attach し終えた段階で、現在ゾーンを初回発行する。
        // プラグイン読み込み時に既にゾーン内にいる場合（dev 再 enable / クライアント起動済みでの新規読込）
        // でも TriggerEngine 等が _currentZone を取得できるようにする。
        _zoneCapture.PublishInitialState();
        // ゾーン初期化の後に戦闘状態も初期化する。戦闘中にプラグインをロード／Reload しても
        // CombatStartedEvent が発行され、予測 TTS・mechanic・同期がその 1 戦から正しく稼働する。
        _combatStateCapture.PublishInitialState();

        Log.Information("[FfxivEchoes] Loaded v{Version} (config v{ConfigVersion}), capture services online",
            PluginInterface.Manifest.AssemblyVersion, Configuration.Version);
    }

    /// <summary>外部からアリーナ寸法計測ウィンドウを開閉する。CommandRouter から呼ぶ用。</summary>
    public void ToggleArenaRuler()
    {
        _arenaRulerWindow.IsOpen = !_arenaRulerWindow.IsOpen;
    }

    /// <summary>
    /// Dispose を try/catch で隔離する。1 個のサブシステムが例外を投げても他に伝播させない。
    /// /xlrestart 時に Dispose が中途で死ぬとフックが残ってゲームクラッシュにつながる。
    /// </summary>
    private static void SafeDispose(IDisposable? d, string name)
    {
        if (d is null) return;
        try
        {
            d.Dispose();
        }
        catch (Exception ex)
        {
            try { Log.Error(ex, "[FfxivEchoes] Dispose '{Name}' failed", name); }
            catch { /* Log すら使えない局面では握り潰す */ }
        }
    }

    public void Dispose()
    {
        Log.Information("[FfxivEchoes] Unloading…");

        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenSettings;
        PluginInterface.UiBuilder.OpenMainUi -= OpenSettings;

        // 1. ネイティブフック（ActionEffectCapture）を最優先で外す。
        //    /xlrestart 時、フックが残ったままアセンブリがアンロードされると、
        //    ゲーム本体が解放済み関数ポインタを呼んでクラッシュする。
        //    各 Dispose を try/catch で隔離し、1 つの失敗が連鎖しないようにする。
        SafeDispose(_actionEffectCapture, nameof(_actionEffectCapture));

        // 2. アクションディスパッチャを止めて新規 TTS/オーバーレイ等を遮断
        SafeDispose(_actionDispatcher, nameof(_actionDispatcher));
        SafeDispose(_ttsHandler, nameof(_ttsHandler));

        // 3. タイムラインノート通知 / 自動 AoE 等
        SafeDispose(_recordingWarmup, nameof(_recordingWarmup));
        SafeDispose(_noteReminder, nameof(_noteReminder));
        SafeDispose(_addObjectAoe, nameof(_addObjectAoe));
        // ActorTrackedAoe は WorldOverlayWindow の live drawer 登録を持つので
        // _worldOverlayWindow 解放より前に外す。
        // PredictedCastReminderService は ActorTrackedAoeService に依存するので先に外す。
        SafeDispose(_predictedCastReminder, nameof(_predictedCastReminder));
        SafeDispose(_upcomingAoePreview, nameof(_upcomingAoePreview));
        SafeDispose(_predictedObjectSpawn, nameof(_predictedObjectSpawn));
        SafeDispose(_branchObserver, nameof(_branchObserver));
        SafeDispose(_phaseTracker, nameof(_phaseTracker));
        SafeDispose(_actorTrackedAoe, nameof(_actorTrackedAoe));
        SafeDispose(_castRotationSnapshot, nameof(_castRotationSnapshot));
        SafeDispose(_pcDetector, nameof(_pcDetector));
        SafeDispose(_autoTelegraph, nameof(_autoTelegraph));
        SafeDispose(_autoAttackTiming, nameof(_autoAttackTiming));
        SafeDispose(_mechanicTrigger, nameof(_mechanicTrigger));
        SafeDispose(_autoArenaCalibration, nameof(_autoArenaCalibration));
        SafeDispose(_raidWideDetector, nameof(_raidWideDetector));
        SafeDispose(_syncOffset, nameof(_syncOffset));

        // 4. スクリプト群
        SafeDispose(_scriptManager, nameof(_scriptManager));

        // 5. トリガーエンジン → 新規 TriggerFired を発生させない
        SafeDispose(_triggerEngine, nameof(_triggerEngine));
        SafeDispose(_variableStore, nameof(_variableStore));

        // 6. ロガー（録画ファイルを確実にフラッシュ）
        SafeDispose(_battleRecorder, nameof(_battleRecorder));

        // 7. トリガー監視・ストア
        SafeDispose(_triggerWatcher, nameof(_triggerWatcher));

        // 8. キャプチャ群（イベントバス購読者）。Framework.Update から外れる
        SafeDispose(_debugChatEcho, nameof(_debugChatEcho));
        SafeDispose(_playerPositionCapture, nameof(_playerPositionCapture));
        SafeDispose(_objectCapture, nameof(_objectCapture));
        SafeDispose(_hpCapture, nameof(_hpCapture));
        SafeDispose(_statusCapture, nameof(_statusCapture));
        SafeDispose(_castCapture, nameof(_castCapture));
        SafeDispose(_zoneCapture, nameof(_zoneCapture));
        SafeDispose(_combatStateCapture, nameof(_combatStateCapture));
        SafeDispose(_combatClock, nameof(_combatClock));

        try { WindowSystem.RemoveAllWindows(); } catch { /* ignore */ }
        SafeDispose(_mainWindow, nameof(_mainWindow));
        SafeDispose(_overlayWindow, nameof(_overlayWindow));
        SafeDispose(_minimapWindow, nameof(_minimapWindow));
        SafeDispose(_upcomingWindow, nameof(_upcomingWindow));
        SafeDispose(_debuffHudWindow, nameof(_debuffHudWindow));
        SafeDispose(_worldOverlayWindow, nameof(_worldOverlayWindow));
        SafeDispose(_arenaRulerWindow, nameof(_arenaRulerWindow));

        try { CommandManager.RemoveHandler(CommandRouter.RootCommand); }
        catch (Exception ex) { try { Log.Warning(ex, "[FfxivEchoes] CommandManager.RemoveHandler failed"); } catch { } }
    }

    private void OnCommand(string command, string args) => _commandRouter.Dispatch(args);

    /// <summary>SPEC.md §13.3「<c>/myplugin → 設定画面を開く</c>」に対応するエントリ。</summary>
    private void OpenSettings() => _mainWindow.Open(MainWindow.DefaultTabId);

    private List<ITab> BuildTabs() => new()
    {
        new HelpTab(),
        new ContentListTab(_triggerStore, _recordingScanner, _tabContext),
        new TriggerEditorTab(_triggerStore, _recordingScanner, _tabContext, _eventBus, _autoGenerator, ObjectTable, ToggleArenaRuler),
        new LiveHudTab(_eventBus, _combatClock),
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
        // /echoes timeline は LiveTimelineWindow 撤去に伴い廃止
        router.Register(new ProfileCommand(_profileStore, Configuration, ChatGui));
        router.Register(new RulerCommand(ToggleArenaRuler));
        router.Register(new TestAoeCommand(_minimapWindow, ObjectTable, ChatGui, Log));
        if (_predictedObjectSpawnLearner is not null)
        {
            router.Register(new LearnSpawnsCommand(
                _predictedObjectSpawnLearner, _triggerStore, ClientState, DataManager, ChatGui, Log));
        }
        if (_luminaActionLookup is not null)
        {
            router.Register(new DumpActionsCommand(
                _triggerStore, _recordingScanner, _luminaActionLookup, PluginInterface, ChatGui, Log));
        }
        router.Register(new HelpCommand(router, ChatGui));

        return router;
    }
}
