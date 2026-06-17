# FFXIV Echoes — Web Demo

ゲームを起動せずに、ライブタイムライン・オーバーレイ表示・トリガー発火の挙動をブラウザで確認するためのモックです。

## 使い方

### ローカルで開く

```bash
# リポジトリルートで
cd demo
# Python が入っていれば：
python -m http.server 8000
# 別タブで http://localhost:8000/ を開く
```

> 直接 `index.html` をダブルクリック（`file://` プロトコル）でも一応動きますが、`fetch('sample-config.json')` がブラウザによってブロックされる場合があります。その場合はインラインの空サンプルにフォールバックするので、JSON を手で貼り付けてください。

### 操作

| 要素 | 動作 |
|------|------|
| ▶ 戦闘開始 | 戦闘相対秒のクロックがスタート。シミュレートされたボスキャストが時刻通りに走る |
| リセット | クロック・履歴をすべて初期化 |
| speed | クロック速度（0.5×/1×/2×/4×）。長い戦闘を素早く確認したい時に |
| role | 自分のロール。`role` 指定のあるノートのフィルタ結果が変わる |
| トリガー定義 (JSON) | 編集して「適用」を押すとシミュレーションに反映 |

## 画面構成

### in-game view（モック）
- **中央テキスト**：`overlay_text` アクションの表示。`size: small/medium/large` と `color: "#RRGGBB"` を反映。フェードイン／アウトあり
- **右下タイマーバー**：`timer_bar` アクション。`warn_at` 時間以内になると点滅警告

### ライブタイムライン
- 過去 10 秒 〜 未来 30 秒の水平スクロール
- 中央の白いラインが「現在時刻」
- **緑線**：`sync_points`
- **黄色線 + ラベル**：`notes`（軽減・LB 等のメモ）
  - `duration` ありなら半透明バーで尺を可視化
  - 自分の `role` と一致しないノートは半透明で表示（通知も飛ばない）
- **青ドット**：進行中のボスキャスト

### チャット出力
- TTS（🔊）、ノート発火（📌）、ボスキャスト（⚡）、戦闘ライフサイクル（黄）の履歴

## 実装範囲

このデモで再現しているもの：

- ✅ TimelineNote の表示（時刻線・ラベル・duration バー・色・role フィルタ）
- ✅ TimelineNote の `advance_warning_sec` での先行通知（TTS + overlay_text）
- ✅ ボスキャスト → トリガーマッチ → アクション発火（cast_id / cast_name の単純一致）
- ✅ アクション：tts / chat_echo / overlay_text / timer_bar
- ✅ overlay_text のフェードイン・アウト（in-game P6 と同じ挙動）
- ✅ timer_bar の warn パルス
- ✅ SyncPoint の表示

このデモで再現していないもの（実プラグインでは動く）：

- ❌ ステータス／HP 変化／オブジェクト出現などのキャプチャ系
- ❌ 複合条件 (all_of/any_of)、変数、stored_position
- ❌ 安置計算プリセット（座標計算）→ direction_call / screen_arrow / field_marker
- ❌ wav 再生（ブラウザで TTS 再生は実装していない、ログ表示のみ）
- ❌ プロファイル切替、インポート／エクスポート、バックアップ
- ❌ FieldMarker / WorldOverlay（3D 投影が必要）

## サンプル JSON 編集ガイド

`sample-config.json` の主なフィールド：

```json
{
  "version": "1.0",
  "zone": "コンテンツ名",
  "auto_settings": { "enable_triggers": true, "show_timeline": true },
  "sync_points": [
    { "id": "ID", "type": "cast_start", "expected_time": 180.0 }
  ],
  "triggers": [
    {
      "id": "trigger_id",
      "type": "cast_start",
      "match": { "cast_id": "0xHEX", "cast_name": "アクション名" },
      "actions": [
        { "type": "tts", "text": "読み上げる文" },
        { "type": "overlay_text", "text": "表示文", "duration": 5,
          "color": "#FF4444", "size": "large" },
        { "type": "timer_bar", "label": "次まで", "duration": 18, "warn_at": 5 }
      ]
    }
  ],
  "notes": [
    { "id": "first_mit", "time": 30, "label": "迅速 + 堅実",
      "role": "tank", "advance_warning_sec": 3, "color": "#FBBF24" },
    { "id": "raid_buff", "time": 120, "label": "全体バフ",
      "duration": 20, "advance_warning_sec": 5 }
  ],
  "_demo_boss_casts": [
    { "time": 8,  "cast_id": "0x9D32", "cast_name": "無の肥大", "cast_time": 4.7 },
    { "time": 25, "cast_id": "0x9D40", "cast_name": "無の追跡", "cast_time": 4.0 }
  ]
}
```

`_demo_boss_casts` は **このデモ専用** のフィールドです。実プラグインでは無視されます。デモではここに書かれた時刻通りに「ボスがキャストを開始した」体でトリガーマッチをシミュレートします。

## Replay Inspector

`replay.html` は **trace.json** を時刻軸で再生し、AoE 描画決定を SVG で可視化する別ページです。
プラグインの `AutoTelegraphService` / `AddObjectAoeService` / `PredictedObjectSpawnService` が
「どこにどの形状を描こうとしたか」「なぜスキップしたか」をブラウザ上で検証するために使います。

### 起動

```bash
cd demo
python -m http.server 8000
# ブラウザで http://localhost:8000/replay.html
```

起動時に `sample-trace.json` を自動ロードします。月の底パラデイグマ → ケツアクアトル 4 体予告 →
確定描画 → 消失 の 30 秒シナリオが入っています。

### 操作

| 要素 | 動作 |
|------|------|
| Trace ファイル | クリック or ドロップで自前の trace.json を読み込み |
| ▶ / ⏸ | 再生 / 一時停止 |
| ⏮ / ⏭ | 直前 / 次のイベントへステップ |
| ⟲ | 先頭に巻き戻し |
| シーカー | スライダで任意時刻へジャンプ |
| speed | 0.25× 〜 4× |

### 画面要素

- **Arena**：北 = 上 / +Z = 南 / +X = 東。半径 m とアリーナ形状（circle / rect）を SVG で描画。
  active AoE が形状 (donut / circle / rect / cone / line / chevron) × 色 × アンカーで重なる
- **Active AoE 一覧**：現在描画中の AoE と残り秒数。predict（オレンジ）/ actor（赤）で色分け
- **Event Log**：現在時刻 ±5 秒のイベント。draw / remove / skip / cast / object_appear が時刻順に並ぶ
- **Skip Reasons**：trace 全体で累積した skip 理由をサービス別に集計

### trace.json schema

完全な仕様は [`../docs/superpowers/specs/2026-05-24-aoe-fix-and-browser-replay-design.md`](../docs/superpowers/specs/2026-05-24-aoe-fix-and-browser-replay-design.md) §3 を参照。
最小要素：

```json
{
  "meta": { "schema_version": "1.0", "zone": "月の底" },
  "arena": { "center_x": 100.0, "center_z": 100.0, "radius_m": 20.0, "shape": "circle" },
  "events": [
    { "t": 0.0, "kind": "cast_start", "cast_id": "0x67BF", "cast_name": "...", "cast_time": 5.0 },
    { "t": 1.0, "kind": "draw_aoe", "id": "...", "service": "...", "shape": "donut",
      "x_world": 88.79, "z_world": 87.36, "radius_m": 6.0, "inner_radius_m": 2.0,
      "duration_sec": 14.0, "color": "#FFA500", "label": "..." },
    { "t": 8.05, "kind": "aoe_skipped", "service": "AutoTelegraphService", "reason": "..." },
    { "t": 15.0, "kind": "remove_aoe", "id": "...", "reason": "duration_expired" }
  ]
}
```

trace.json は C# 側 `src/FfxivEchoes.Replay/` の harness が録画 jsonl から生成する想定（実装は別タスク）。

## 関連

- 実プラグイン本体：[`../src/FfxivEchoes/`](../src/FfxivEchoes/)
- トリガースキーマ仕様：[`../trigger-schema.json`](../trigger-schema.json)
- メイン仕様書：[`../SPEC.md`](../SPEC.md)
- ロードマップ：[`../roadmap.md`](../roadmap.md)
- Replay 設計書：[`../docs/superpowers/specs/2026-05-24-aoe-fix-and-browser-replay-design.md`](../docs/superpowers/specs/2026-05-24-aoe-fix-and-browser-replay-design.md)
