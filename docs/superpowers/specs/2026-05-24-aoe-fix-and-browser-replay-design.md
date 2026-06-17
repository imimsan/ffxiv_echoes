# AoE 表示修正 & ブラウザ Replay Inspector — 設計書

作成日: 2026-05-24
対象ブランチ: `feature/minimap-boss-roles`（既存）または新規 `feature/aoe-fix-and-replay`
関連: [predicted-object-spawn-design.md](../../predicted-object-spawn-design.md)

## 1. 目的

FFXIV Echoes プラグインの **AoE 表示が不安定**な状態を、ゲームを起動せず **録画 jsonl を再生して検証** できるようにする。
そのうえで、月の底コンテンツの 18 録画を電話帳に使って具体的なバグを特定 → 修正する。

### 1.1 ユーザー報告の症状

ブレストで確認した「動いていない」具体的な症状は：

- **予告 AoE が出ない / 遅い** — `PredictedObjectSpawnService` 由来の事前予告がそもそも描画されない、または object 出現直前すぎる
- **実 AoE の位置がおかしい** — `AutoTelegraphService` / `AddObjectAoeService` が actor の実位置でなく中央や placeholder 位置 (100,_,100) で描く
- **形状が間違う** — donut が circle、rect が cone、半径違い、内径欠落、ローテーション 90° ズレ等

これらを **録画から客観的に検証可能**にするのが本タスクの目的。

### 1.2 スコープ

**含む**:
- 録画 jsonl を C# 側で headless replay → AoE 描画決定を JSON トレース化する仕組み
- そのトレースをブラウザ SVG で時間軸再生する viewer
- Lumina (Action sheet) を実機 Dalamud 抜きで参照可能にする dump 機構
- 月の底コンテンツに絞った 1 ボス、3 症状の修正

**含まない**:
- 他コンテンツ（雲廊・暗闇の領域・武神の闘技場）への展開（同じ手順で後追い）
- リアルタイム再生（ステップ実行 / スキップは OK）
- ImGui 描画の完全再現（UI 機能の検証ではなく「何を描こうとしたか」の検証が目的）

## 2. アーキテクチャ

```
┌─ Recording (.jsonl) ────────────────────────────────────┐
│  %AppData%/.../recordings/月の底/*.jsonl                │
└──────────────────────────────────────────────────────────┘
                          │
                          ▼
┌─ FfxivEchoes.Replay (NEW console app) ───────────────────┐
│  ├─ MockServices/ (Dalamud インターフェース実装)          │
│  ├─ JsonlReplayer    (jsonl → IEventBus 発火)            │
│  ├─ TraceRecorder    (描画決定を JSON にキャプチャ)       │
│  └─ Program.cs       (CLI)                              │
└──────────────────────────────────────────────────────────┘
                          │
                          ▼
┌─ Trace Output (trace.json) ─────────────────────────────┐
│  arena: {center, radius, shape}                          │
│  events: [                                               │
│    {t, kind: "cast_start"|"object_appear"|...},          │
│    {t, kind: "draw_aoe", id, service, shape, x, z,      │
│         radius, innerRadius, fanDeg, halfWidth,          │
│         duration, color, trigger_event, ...},            │
│    {t, kind: "aoe_skipped", service, reason, action},    │
│    {t, kind: "remove_aoe", id}                          │
│  ]                                                       │
└──────────────────────────────────────────────────────────┘
                          │
                          ▼
┌─ demo/replay.html (NEW) ────────────────────────────────┐
│  trace.json を読み込み、SVG arena に時間軸で再生         │
│  ▶ ⏸ ⏮ ⏭ slider speed 0.5/1/2/4×                       │
│  active AoE 一覧、skip reason 集計                       │
└──────────────────────────────────────────────────────────┘
```

### 2.1 設計の核心

プラグインの AoE 解決ロジック（`AutoTelegraphService`, `AddObjectAoeService`, `PredictedObjectSpawnService`, `AoeResolver` 等）は現状 Dalamud の `IFramework` / `IObjectTable` / `IPluginLog` / `IDataManager` 経由でサービス参照する。
これらは **インターフェース注入**なので、**Dalamud SDK に依存せず mock 実装に差し替え可能**。これが headless 化のキーになる。

唯一例外的に Lumina (`IDataManager.GetExcelSheet<Action>` 等) は重い。これは「実機で 1 度だけ dump → JSON コミット」戦略（後述 §5）で回避する。

## 3. データモデル（trace.json）

```json
{
  "meta": {
    "schema_version": "1.0",
    "recording_path": "...\\2026-05-15_10-05-17.jsonl",
    "zone": "月の底",
    "boss": "ゾディアーク",
    "trace_generated_at": "2026-05-24T...",
    "harness_version": "0.1.0"
  },
  "arena": {
    "center_x": 100.0,
    "center_z": 100.0,
    "radius_m": 20.0,
    "shape": "circle"
  },
  "events": [
    {
      "t": 9.123,
      "kind": "cast_start",
      "cast_id": "0x67BF",
      "cast_name": "パラデイグマ",
      "source": "ゾディアーク",
      "source_id": 3001,
      "cast_time": 5.0
    },
    {
      "t": 14.0,
      "kind": "draw_aoe",
      "id": "predict_67BF_14388_idx0",
      "service": "PredictedObjectSpawnService",
      "trigger_event": { "type": "cast_start", "cast_id": "0x67BF", "t": 9.123 },
      "shape": "donut",
      "anchor": "static",
      "x_relative": -11.21,
      "z_relative": -12.64,
      "x_world": 88.79,
      "z_world": 87.36,
      "rotation_rad": 0.0,
      "radius_m": 6.0,
      "inner_radius_m": 2.0,
      "duration_sec": 14.0,
      "color": "#FFA500",
      "label": "予告: ケツアクアトル"
    },
    {
      "t": 14.0,
      "kind": "aoe_skipped",
      "service": "AutoTelegraphService",
      "reason": "target_world is null",
      "action": { "action_id": "0x179C", "source": "ゾディアーク", "t": 14.0 }
    },
    {
      "t": 28.5,
      "kind": "remove_aoe",
      "id": "predict_67BF_14388_idx0",
      "reason": "duration_expired"
    }
  ]
}
```

## 4. ReplayHarness の構成

新規プロジェクト `src/FfxivEchoes.Replay/FfxivEchoes.Replay.csproj`（console exe、`Microsoft.NET.Sdk`、Dalamud SDK 非依存）。

### 4.1 ファイル一覧

```
src/FfxivEchoes.Replay/
├── FfxivEchoes.Replay.csproj
├── Program.cs                    CLI 引数 + 全体オーケストレーション
├── MockServices/
│   ├── MockFramework.cs          IFramework: RunOnFrameworkThread は同期、Update を時刻駆動
│   ├── MockObjectTable.cs        IObjectTable: object_appear で登録、object_disappear で外す
│   ├── MockPluginLog.cs          IPluginLog: stdout + trace 経由
│   ├── MockChatGui.cs            IChatGui: no-op
│   ├── MockDataManager.cs        IDataManager: actions.json (§5) を Lumina 風 API で返す
│   ├── MockClientState.cs        IClientState: zone 名固定
│   ├── MockGameInteropProvider.cs 必要なら最小実装 or 例外で fail-fast
│   └── ...                        他の最小限の Dalamud サービス
├── JsonlReplayer.cs              jsonl 1 行 → 適切な Event 型 → IEventBus.Publish
├── Capture/
│   ├── TraceRecorder.cs          描画呼出をフックして trace 蓄積
│   ├── MinimapWindowStub.cs      MinimapWindow.AddArenaView を捕捉する代替実装
│   └── ActorTrackedAoeProbe.cs   ActorTrackedAoeService の出力を観測
├── Aggregator.cs                 複数 trace を集計 → サマリ（"cast 0x67BF: 18 回中 12 回 skip"）
└── README.md
```

### 4.2 CLI

```bash
dotnet run --project src/FfxivEchoes.Replay -- \
    --recording "<path or glob>" \
    --zone "月の底" \
    --trigger-file "%AppData%/.../triggers/月の底.json" \
    --actions-data "data/actions/月の底.json" \
    --output "out/trace.json" \
    [--aggregate "out/summary.json"]

# 複数録画一括処理：
dotnet run --project src/FfxivEchoes.Replay -- \
    --recording "%AppData%/.../recordings/月の底/*.jsonl" \
    --aggregate out/summary.json
```

### 4.3 オーケストレーション

```csharp
// Program.cs 概要
var actionsData = MockDataManager.LoadFromJson(opts.ActionsDataPath);
var dataManager = new MockDataManager(actionsData);
var framework = new MockFramework();
var objectTable = new MockObjectTable();
var log = new MockPluginLog();
var chatGui = new MockChatGui();

var eventBus = new EventBus();
var traceRecorder = new TraceRecorder();

// 既存サービスをそのまま組み立てる（Plugin.cs と同じ DI 順序）
var triggerStore = new TriggerStore(...);
triggerStore.Load(opts.TriggerFilePath);

var autoTelegraph = new AutoTelegraphService(framework, eventBus, objectTable, ..., log);
var predictedSpawn = new PredictedObjectSpawnService(framework, eventBus, triggerStore, log);
var addObjectAoe = new AddObjectAoeService(framework, eventBus, objectTable, triggerStore, log);
// MinimapWindow の代わりに TraceRecorder にフックする

traceRecorder.Subscribe(eventBus, framework);

// 再生
var replayer = new JsonlReplayer(eventBus, framework, objectTable);
replayer.Play(opts.RecordingPath);  // ここで時刻順に Publish

traceRecorder.WriteTo(opts.OutputPath);
```

### 4.4 MinimapWindow の扱い

`MinimapWindow.AddArenaView` を直接呼ぶ既存コード（`ActorTrackedAoeService` など）がいくつかある。これを headless 化するため：

- **A**: `MinimapWindow` を interface `IMinimapSink` に抽象化し、テスト時は `TraceRecorder` を sink に差し込む（**推奨**）
- B: `MinimapWindow` 自体をスタブ実装で差し替える（侵襲が大きい）

A を採用。`IMinimapSink { void AddArenaView(ArenaView view); void RemoveArenaView(string id); ... }` を切り出し、本番は `MinimapWindow` が実装、テスト時は `TraceRecorder` が実装する。

## 5. Lumina (Action sheet) の dump 機構

### 5.1 dump コマンド

プラグインに `/echoes dump-actions [<zone>]` を追加：

- 指定 zone の trigger ファイル + 既存録画から「使われている action_id」を全列挙
- Lumina で各 action の name / cast_type / effect_range / x_axis_modifier / omen / cast_time を取得
- `data/actions/<zone>.json` に書き出し

### 5.2 actions.json スキーマ

```json
{
  "zone": "月の底",
  "generated_at": "2026-05-24T...",
  "actions": [
    {
      "id": "0x67BF",
      "id_dec": 26559,
      "name": "パラデイグマ",
      "cast_type": 1,
      "effect_range": 0.0,
      "x_axis_modifier": 0.0,
      "omen": null,
      "cast_time_ms": 5000
    },
    {
      "id": "0x179C",
      "id_dec": 6044,
      "name": "アルゲドン",
      "cast_type": 7,
      "effect_range": 6.0,
      "x_axis_modifier": 2.0,
      "omen": "donut_6_2",
      "cast_time_ms": 0
    }
  ]
}
```

### 5.3 MockDataManager

`IDataManager` の最小限のサーフェスを実装。プラグイン内で `dataManager.GetExcelSheet<Action>()` を呼んでいる箇所を mock の `IActionLookup` 経由に書き直し（最小侵襲：薄いラッパを `AoeResolver` 内に追加）。

## 6. ブラウザ Replay Viewer

### 6.1 構成

```
demo/
├── index.html              (既存、変更なし)
├── demo.js                 (既存、共有部分を lib/ に抽出)
├── style.css               (既存)
├── replay.html             (NEW)
├── replay.js               (NEW)
└── lib/
    ├── arena-svg.js        アリーナ + AoE 形状の SVG レンダラ
    └── trace-player.js     trace.json の時刻軸再生制御
```

### 6.2 replay.html 画面要素

```
┌──────────────────────────────────────────────────────────┐
│  FFXIV Echoes — Replay Inspector                         │
├──────────────────┬───────────────────────────────────────┤
│ Trace ファイル   │  ┌─────────────────────────────────┐  │
│  [load .json]    │  │ Arena SVG (左 = 北)              │  │
│ Expected ファイル│  │  ・ボス / object actor の現在位置 │  │
│  [load .json]    │  │  ・active AoE: 形 × 色 × アンカー │  │
│                  │  │  ・スケール: m / pixel           │  │
│ ▶ ⏸ ⏮ ⏭         │  └─────────────────────────────────┘  │
│ ━━●━━━━━━━━━━━   │                                        │
│ t=14.0 / 620.0   │  ┌─ Event Log (current ±5s) ───────┐  │
│                  │  │ 14.0  draw  predict_67BF_idx0    │  │
│ speed 0.5/1/2/4× │  │ 13.9  cast_complete 0x67BF       │  │
│                  │  │ 13.5  obj_appear 14388 @-11,-12  │  │
│ ┌Active AoE────┐ │  └──────────────────────────────────┘  │
│ │ 4 zones      │ │                                        │
│ │ predict ×4   │ │  ┌─ Skip Reasons (集計) ──────────┐  │
│ │ donut r=6m   │ │  │ AutoTelegraph                    │  │
│ │ 12.0→26.0    │ │  │   target_world null: 4 回         │  │
│ └──────────────┘ │  │ AddObjectAoe                     │  │
│                  │  │   placeholder pos: 3 回           │  │
│                  │  └──────────────────────────────────┘  │
└──────────────────┴───────────────────────────────────────┘
```

### 6.3 arena-svg.js

既存 `demo/demo.js` の `renderArenaSvg` を一般化：

```js
// 入力: { shape, x, z, radius, innerRadius, fanDeg, halfWidth, rotationRad, color }
// 出力: SVG <g> 要素文字列
function renderAoe(aoe, arena) { ... }

// 入力: { center_x, center_z, radius_m, shape }
// 出力: SVG arena container
function renderArena(arena) { ... }
```

donut / circle / rect / cone / line を arena-relative 座標で配置できる。

## 7. 並列 Agent 分担

`superpowers:subagent-driven-development` で実行。

### Phase 1 (3 agent 並列、background)

| Track | Agent type | Output |
|---|---|---|
| **T1 録画解析** | feature-dev:code-explorer | `out/aoe-bug-survey-月の底.md`：18 録画から判明したバグ症状の列挙、頻度、該当 cast_id |
| **T2 Replay Harness 雛形** | general-purpose | `src/FfxivEchoes.Replay/` プロジェクト一式（MockServices + JsonlReplayer + 最小 TraceRecorder、dummy actions.json で動く）|
| **T3 dump-actions + actions.json** | general-purpose | `/echoes dump-actions` 実装 + `data/actions/月の底.json` 生成 + AoeResolver の Lumina 経路を `IActionLookup` 抽象化 |

### Phase 2 (T2+T3 完了後、1 agent)

| Track | Agent type | Output |
|---|---|---|
| **T4 Browser Viewer** | general-purpose | `demo/replay.html` + `demo/lib/arena-svg.js` + `demo/lib/trace-player.js`、サンプル trace.json で動作確認 |

### Phase 3 (T1〜T4 完了後、自分 + reviewer)

| Track | Agent type | Output |
|---|---|---|
| **T5 AoE 修正** | claude (自分) + feature-dev:code-reviewer | T1 で挙げたバグを T2+T3+T4 で再現 → 修正 → 同じ trace で再検証。月の底 18 録画で 3 症状が解消するまでループ |

### 並列の独立性

- T1: read-only 解析。コード変更なし
- T2: 新規プロジェクト、既存ファイル変更なし（ただし `IMinimapSink` 抽出は必要、これは T2 内で完結させる）
- T3: 新規コマンド + 新規 actions.json、`AoeResolver` への薄いラッパ追加（既存テストには触らない）

T2 と T3 は両方とも一部で既存 `Triggers/` に手を入れる可能性があるが、対象ファイルが異なる（T2 は MinimapWindow 周り、T3 は AoeResolver / Commands）ので衝突しない見込み。

## 8. 完了条件

- `src/FfxivEchoes.Replay/` がビルド・実行できる
- `data/actions/月の底.json` がコミットされ、MockDataManager が読める
- `demo/replay.html` で `out/trace.json` が再生でき、AoE 形状・位置が SVG で見える
- 月の底 18 録画で T1 が挙げた症状が trace 上で確認できる
- T5 修正後、同じ trace で 3 症状の頻度が改善している（理想は 0 になる）

## 9. 影響範囲

### 新規

- `src/FfxivEchoes.Replay/` 一式
- `src/FfxivEchoes/Commands/Handlers/DumpActionsCommand.cs`
- `src/FfxivEchoes/Triggers/IActionLookup.cs`（あるいは `Lumina/`）
- `data/actions/月の底.json`
- `demo/replay.html`, `demo/replay.js`, `demo/lib/arena-svg.js`, `demo/lib/trace-player.js`
- `out/` (gitignore、CLI 出力先)

### 拡張

- `src/FfxivEchoes/Triggers/AoeResolver.cs` — Lumina 直接参照を `IActionLookup` 経由に差し替え
- `src/FfxivEchoes/Windows/MinimapWindow.cs` — `IMinimapSink` 実装に
- `src/FfxivEchoes/Plugin.cs` — `IMinimapSink` / `IActionLookup` の DI 登録、DumpActionsCommand 登録
- `demo/demo.js` — 共有可能関数を `lib/arena-svg.js` に抽出
- `.gitignore` — `out/` を追加

### 修正対象（Phase 3 / T5）

- `PredictedObjectSpawnService.cs` — 予告タイミング / 位置解決
- `AutoTelegraphService.cs` — target_world null skip 条件、位置解決
- `AddObjectAoeService.cs` — placeholder 座標 skip、actor 出現待ち
- 必要に応じて `ActorTrackedAoeService.cs`, `AoeResolver.cs`, `ObjectAoeRuleResolver.cs`

T1 のレポートで優先順位が決まる。

## 10. リスクと対策

| リスク | 対策 |
|---|---|
| Mock サービス実装の網羅性不足で実コードが NRE 等で落ちる | Phase 1 T2 内で月の底 jsonl 1 ファイル replay が通るまで完成とみなさない |
| Lumina 依存が思った以上に深い | dump-actions で「必要 action ID 全列挙」してから IActionLookup 抽象化を当てる順序にし、抜けを早期検出 |
| Browser SVG の座標系（左 = 北）と FFXIV 座標系（+Z = 南）混乱 | `arena-svg.js` で 1 箇所に変換ロジックを集約、ユニットテスト相当のアサーションを inline で持つ |
| 既存 PredictedObjectSpawnLearner の uncommitted 変更とぶつかる | T1〜T4 では当該ファイルに触らない。T5 で初めて触る |
| Phase 3 で修正範囲が広すぎてバグが増える | T5 は 1 症状ずつ修正 → 各回 trace 再生で回帰確認 |

## 11. 非目標

- 完璧な再現：実機 Dalamud の挙動 100% 一致は目指さない（Framework タイミング誤差等）
- 1 トリガー仕様の更新：本タスクはあくまで「現状仕様のままで描画が間違う」を直す。仕様変更（例：新しい shape 追加）は別タスク
- 全ゾーン対応：月の底だけ。他は同じ手順を後追いで適用できる土台を作る
