# FfxivEchoes.Replay — Headless Replay Harness

Dalamud 非依存で動く console exe。録画 jsonl を再生して、プラグインの
AoE 描画決定を JSON trace としてキャプチャする。

設計書: [`docs/superpowers/specs/2026-05-24-aoe-fix-and-browser-replay-design.md`](../../docs/superpowers/specs/2026-05-24-aoe-fix-and-browser-replay-design.md)

## 目的

FFXIV を起動せず、録画 jsonl だけで AoE 表示ロジックの不具合を再現・検証できるようにする。
最終的にこの trace を [`demo/replay.html`](../../demo/replay.html)（T4 で実装予定）が読み込んで
ブラウザ SVG で時間軸再生する。

## ビルド

```powershell
dotnet build -c Debug -p:Platform=x64 src/FfxivEchoes.Replay/FfxivEchoes.Replay.csproj
```

`-p:Platform=x64` 必須（CLAUDE.md memory `build_platform_x64` 参照）。

Dalamud DLL のパスは `DALAMUD_HOME` 環境変数があればそれを、無ければ
`%AppData%/XIVLauncher/addon/Hooks/dev` を既定値として参照する。

## 使い方

```powershell
dotnet run --project src/FfxivEchoes.Replay -c Debug -- `
    --recording sample-recording.jsonl `
    --zone "極エヌオー討滅戦" `
    --output out/trace-sample.json
```

引数：

| flag | 略 | 説明 |
|------|----|------|
| `--recording <path>` | `-r` | 入力 jsonl（必須） |
| `--zone <name>`     | `-z` | ゾーン名（未指定なら meta 行から自動） |
| `--trigger-file <path>` | `-t` | トリガー定義（Phase 1 では未 wire） |
| `--output <path>`   | `-o` | trace.json 出力先（既定: `out/trace.json`） |

## 出力スキーマ

設計書 §3 に従う。Phase 1 は最低限のフィールドのみで、後段で精緻化される。

```json
{
  "meta": {
    "schema_version": "1.0",
    "recording_path": "...",
    "zone": "...",
    "harness_version": "0.1.0",
    "trace_generated_at": "..."
  },
  "arena": {
    "center_x": 100.0,
    "center_z": 100.0,
    "radius_m": 20.0,
    "shape": "circle"
  },
  "events": [
    { "t": 0.000, "kind": "combat_start" },
    { "t": 3.456, "kind": "cast_start", "cast_id": "0x67BF", "cast_name": "...", "source_id": 12345 },
    { "t": 14.0, "kind": "draw_aoe", "id": "draw_00000", "service": "...", ... },
    { "t": 28.5, "kind": "remove_aoe", "id": "draw_00000", "reason": "..." }
  ]
}
```

`kind` の種類：

- `combat_start` / `combat_end` / `zone_change`
- `cast_start` / `cast_complete` / `cast_cancel` / `action_used`
- `status_gain` / `status_lose` / `hp_change`
- `object_appear` / `object_disappear`
- `draw_aoe` — `IMinimapSink.AddArenaView` 呼出
- `suppress_auto_lumina` — `IMinimapSink.SuppressAutoLuminaForCast` 呼出
- `minimap_clear` — `IMinimapSink.Clear` 呼出
- `aoe_skipped` — サービスがスキップを自発的に報告した場合（Phase 2 以降で使用）

## アーキテクチャ

```
jsonl ──▶ JsonlReplayer ──Publish──▶ InMemoryEventBus
                                            │
                                            ├──▶ TraceRecorder (SubscribeAll)
                                            │       └─ source events を trace に蓄積
                                            │
                                            └──▶ AoE services (Phase 2+ で wire 予定)
                                                    └─ IMinimapSink.AddArenaView を呼ぶ
                                                        ↓
                                                   TraceRecorder (IMinimapSink 実装)
                                                       └─ draw_aoe を trace に蓄積
```

### IMinimapSink

`src/FfxivEchoes/Windows/IMinimapSink.cs` — `MinimapWindow` の public メソッドを
抽象化したインターフェース。本番は `MinimapWindow` が実装、replay は
`TraceRecorder` が実装。

- `AutoTelegraphService` / `AddObjectAoeService` / `PredictedCastReminderService` の
  `_minimap` フィールド型を `IMinimapSink` に変更済み。
- `Plugin.cs` は引き続き具象 `MinimapWindow` を渡す（暗黙的 upcast）。

## Phase 1 の制限

現状の harness は **JsonlReplayer + TraceRecorder のみ** wire 済み。AoE 解決
サービス（`AutoTelegraphService`, `AddObjectAoeService`, `PredictedObjectSpawnService`
等）は wire されていないため `draw_aoe` イベントはまだ trace に出ない。

これらを wire するには以下が必要：

- `MockObjectTable` を `IObjectTable` 実装に拡張（または完全モックを別途用意）
- `WorldOverlayWindow` のスタブ実装（`IGameGui` 依存を断つ）
- `TriggerStore` / `RecordingScanner` の構築（`IDalamudPluginInterface.ConfigDirectory` の mock）
- T3 が提供する `IActionLookup` の JSON dump 実装（`data/actions/<zone>.json` を読む）

詳細は設計書 §4.3 を参照。

## 動作確認

```powershell
# サンプル
dotnet run --project src/FfxivEchoes.Replay -c Debug -- `
    --recording sample-recording.jsonl --output out/trace-sample.json

# 月の底実録画
dotnet run --project src/FfxivEchoes.Replay -c Debug -- `
    --recording "$env:AppData/XIVLauncher/pluginConfigs/FfxivEchoes/recordings/月の底/2026-05-15_10-05-17.jsonl" `
    --output out/trace-tsuki.json
```

trace に少なくとも 1 件の `cast_start` が含まれていれば正常。

## ファイル構成

```
src/FfxivEchoes.Replay/
├── FfxivEchoes.Replay.csproj
├── Program.cs                       CLI + orchestration
├── JsonlReplayer.cs                 jsonl → IEventBus
├── Capture/
│   └── TraceRecorder.cs             IMinimapSink + SubscribeAll(IEventBus)
├── MockServices/
│   ├── MockPluginLog.cs             IPluginLog (stdout)
│   ├── MockFramework.cs             IFramework (時刻駆動 Update)
│   └── MockObjectTable.cs           軽量 actor ストレージ（Phase 1 は IObjectTable 非実装）
└── README.md
```
