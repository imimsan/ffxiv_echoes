# FF14 絶コンテンツ攻略支援プラグイン 仕様書

## 1. 概要

### 1.1 目的

FF14の絶コンテンツ攻略を支援する、Dalamudプラットフォーム上で動作する個人用プラグイン。汎用設計により、絶妖星乱舞（2026年6月2日実装予定）を含む全ての絶コンテンツ、および既存の極・零式コンテンツに対応する。

### 1.2 基本コンセプト

戦闘中に観測されるイベント（ボスキャスト・デバフ付与・アクション発動など）を検知し、ユーザーが事前に設定したタイミングで音声読み上げ・画面表示・安置位置指示などのリアクションを発動する。設定はコンテンツごとのJSONファイルとして管理される。

### 1.3 設計の核心

戦闘ログを録画してそれを素材に設定を組み立てる、というワークフローを採用する。手書きでJSONを書くのではなく、観測されたイベントの一覧（キャストID・ステータスIDなど）をGUI上で確認しながら、必要なものだけトリガー化する。これにより、新コンテンツでも自分でIDを調査することなく設定が可能になる。

### 1.4 スコープ外

以下の機能は意図的に実装しない：

- 自動回避・自動移動
- 自動スキル発動・オートローテーション
- 画像認識による視覚判断
- 他プレイヤーへのリアルタイム情報共有
- ギミックの自動処理

これらは規約上のリスクが高く、本ツールは「情報を表示・読み上げる」までに留める。

### 1.5 動作環境

- FFXIV（日本語クライアント）
- XIVLauncher + Dalamud（最新版）
- C# / .NET（Dalamud SDK仕様に準拠）

---

## 2. アーキテクチャ

### 2.1 コンポーネント構成

```
┌─────────────────────────────────────────┐
│ Dalamud Plugin (C#)                      │
├─────────────────────────────────────────┤
│                                          │
│  ┌──────────────┐  ┌──────────────┐    │
│  │ Event Capture│  │ Object Table │    │
│  │              │  │ Monitor      │    │
│  └──────┬───────┘  └──────┬───────┘    │
│         │                 │             │
│         ▼                 ▼             │
│  ┌─────────────────────────────────┐   │
│  │ Recorder (条件付き起動)          │   │
│  └──────┬──────────────────────────┘   │
│         │                               │
│         ▼                               │
│  ┌────────────┐  ┌──────────────────┐  │
│  │ Log Files  │  │ Trigger Engine   │  │
│  └────────────┘  └────────┬─────────┘  │
│                           │             │
│                           ▼             │
│  ┌─────────────────────────────────┐   │
│  │ Action Dispatcher                │   │
│  │  ├ TTS / WAV                     │   │
│  │  ├ Overlay (Text/Timer/Arrow)    │   │
│  │  ├ Field Marker                  │   │
│  │  └ SafeZone Calculator           │   │
│  └─────────────────────────────────┘   │
│                                          │
│  ┌─────────────────────────────────┐   │
│  │ Settings UI (ImGui)              │   │
│  │  ├ Timeline View                 │   │
│  │  ├ Aggregate View                │   │
│  │  ├ Trigger Editor                │   │
│  │  └ Live HUD Configuration        │   │
│  └─────────────────────────────────┘   │
│                                          │
│  ┌─────────────────────────────────┐   │
│  │ Live HUD Overlay                 │   │
│  │  └ Hybrid Timeline (sync対応)    │   │
│  └─────────────────────────────────┘   │
│                                          │
└─────────────────────────────────────────┘
```

### 2.2 データフロー

1. **観測フェーズ**：ゲーム内イベントをEvent Captureが受信、Object Tableをポーリング
2. **記録フェーズ**：コンテンツ設定で自動記録ONの場合、Recorderがログをファイル保存
3. **発動フェーズ**：Trigger Engineが登録済みトリガーと観測イベントを照合、Action Dispatcherが反応を実行
4. **編集フェーズ**：Settings UIで保存済みログを読み込み、トリガー定義を編集

### 2.3 ファイル配置

```
%AppData%/XIVLauncher/pluginConfigs/MyPlugin/
├── config.json                          # プラグイン全体設定
├── recordings/                          # 戦闘ログ
│   ├── 極エヌオー討滅戦/
│   │   ├── 2026-05-05_22-13-45.jsonl
│   │   └── ...
│   └── 絶妖星乱舞/
│       └── ...
├── triggers/                            # トリガー定義（コンテンツ単位）
│   ├── 極エヌオー討滅戦.json
│   ├── 絶妖星乱舞.json
│   └── ...
├── triggers_backup/                     # 自動バックアップ
│   └── 極エヌオー討滅戦/
│       └── 2026-05-05_22-13-45.json
└── profiles/                            # ジョブ別プロファイル
    ├── default.json
    └── tank.json
```

### 2.4 名前

プラグイン名は実装開始時に確定。本仕様書中では仮称として `MyPlugin` を使用。

---

## 3. データモデル

### 3.1 録画ログ（JSON Lines形式）

戦闘単位で1ファイル。1行1イベント。

```jsonl
{"meta":true,"zone":"極エヌオー討滅戦","start_time":"2026-05-05T22:13:45.123Z","party":[{"name":"自分","job":"BLM","role":"DPS"},...]}
{"time":0.000,"type":"combat_start"}
{"time":3.456,"type":"cast_start","source":"エヌオー","source_id":12345,"cast_id":"0x9D32","cast_name":"無の肥大","target":null,"cast_time":4.7}
{"time":8.156,"type":"action_used","source":"エヌオー","action_id":"0x9D33","action_name":"無の肥大","targets":[]}
{"time":15.234,"type":"status_gain","source":"エヌオー","target":"自分","status_id":4321,"status_name":"無の追跡","duration":18.0,"stacks":1}
{"time":33.234,"type":"status_lose","target":"自分","status_id":4321}
{"time":120.500,"type":"hp_change","actor":"エヌオー","hp_pct":74.5}
{"time":600.123,"type":"combat_end","result":"clear"}
```

メタ情報は1行目に記録。各イベントは戦闘開始からの相対秒（`time`）を持つ。

### 3.2 トリガー定義

JSONスキーマは `trigger-schema.json` を参照。コンテンツ単位で1ファイル。

```json
{
  "version": "1.0",
  "zone": "極エヌオー討滅戦",
  "auto_settings": {
    "enable_triggers": true,
    "auto_record": false,
    "show_timeline": true
  },
  "metadata": {
    "created_at": "2026-05-05T22:13:45Z",
    "last_modified": "2026-05-06T20:30:00Z",
    "notes": "極エヌオー初期版"
  },
  "sync_points": [
    {"id": "phase2_start", "type": "cast_start", "cast_id": "0x9D40", "expected_time": 180.0}
  ],
  "triggers": [
    {
      "id": "muno_higai_warning",
      "enabled": true,
      "type": "cast_start",
      "match": { "cast_id": "0x9D32" },
      "actions": [
        { "type": "tts", "text": "外周の無から離れる" },
        { "type": "overlay_text", "text": "外周回避", "duration": 5 }
      ]
    }
  ],
  "ignored_events": [
    { "type": "cast_start", "cast_id": "0x9D01", "reason": "全体攻撃の演出詠唱" }
  ]
}
```

### 3.3 プロファイル

ジョブ別・用途別のトリガーセット切り替え。

```json
{
  "name": "tank_profile",
  "display_name": "タンク用",
  "active_triggers": {
    "極エヌオー討滅戦": ["muno_higai_warning", "tank_swap_call", ...],
    "絶妖星乱舞": [...]
  }
}
```

同じトリガー定義ファイルから、プロファイルごとに有効化するトリガーIDを切り替える。

---

## 4. イベント検知仕様

### 4.1 検知タイプ

| タイプ | 説明 | 主な用途 |
|--------|------|----------|
| `cast_start` | 敵がキャストを開始した瞬間 | ギミック予告、最頻出 |
| `cast_complete` | キャストが完了した瞬間 | 即時ヒット系の反応 |
| `cast_cancel` | キャストが中断された | 中断検知 |
| `action_used` | 詠唱なしでアクション発動 | 即時技の検知 |
| `status_gain` | ステータス（バフ/デバフ）付与 | デバフ反応 |
| `status_lose` | ステータス消失 | デバフ消失通知 |
| `status_update` | スタック数や残り時間の変化 | スタック警告 |
| `hp_change` | アクターHPが変化（閾値判定用） | フェーズ移行検知 |
| `zone_change` | マップ移動 | コンテンツ突入/退出 |
| `combat_start` | 戦闘開始 | タイムライン基点 |
| `combat_end` | 戦闘終了 | クリア/ワイプ判定 |
| `object_appear` | フィールドにオブジェクト出現 | 雑魚・設置物検知 |
| `object_disappear` | オブジェクト消失 | 同上 |
| `timeline_elapsed` | 戦闘開始からN秒経過 | タイムライン駆動トリガー |

### 4.2 マッチ条件

各検知タイプで指定可能なマッチ条件：

#### 共通条件
- `id` または `name`：イベント識別子
- `source`：発動者（ボス名・雑魚名）。省略時は任意
- `source_id`：発動者のObjectID（特定インスタンスを狙う場合）

#### キャスト系
- `cast_id`：アクションID（16進文字列）
- `cast_name`：アクション名（日本語）
- `cast_time`：詠唱時間の範囲指定 `{ "min": 4.5, "max": 5.0 }`

#### ステータス系
- `status_id`：ステータスID
- `status_name`：ステータス名
- `target`：対象種別（後述）
- `duration_range`：持続時間の範囲 `{ "min": 22, "max": 24 }` ← 短デバフ/長デバフ分岐の核心
- `stacks`：スタック数 `{ "equals": 3 }` または `{ "min": 2 }`

#### HP系
- `hp_pct`：HP割合 `{ "below": 75 }` `{ "above": 50 }`
- `actor`：対象アクター

#### タイムライン系
- `time`：戦闘開始からの経過時間（秒）
- `tolerance`：許容誤差（デフォルト ±2秒）

### 4.3 対象識別

`target` フィールドで指定：

| 値 | 意味 |
|------|------|
| `self` | 自分 |
| `other` | 自分以外 |
| `tank` | タンクロール（MT/ST含む） |
| `mt` | メインタンク |
| `st` | サブタンク |
| `healer` | ヒーラー |
| `h1` / `h2` | 純ヒーラー / バリアヒーラー |
| `dps` | DPS |
| `melee` / `ranged` / `caster` | DPSサブカテゴリ |
| `marker_a` 〜 `marker_h` | フィールドマーカーA〜H |
| `marker_1` 〜 `marker_8` | 数字マーカー1〜8 |
| `party` | PT全員 |
| `any` | 誰でも |

複数指定可能：`["self", "tank"]` のように配列で OR 条件。

### 4.4 複合条件（AND/OR）

`conditions` フィールドで複合条件を表現：

```json
{
  "type": "status_gain",
  "match": { "status_id": 1234 },
  "conditions": {
    "all_of": [
      { "target": "self" },
      { "duration_range": { "min": 22, "max": 24 } }
    ]
  }
}
```

`all_of`（AND）と `any_of`（OR）をサポート。ネスト可能。

### 4.5 状態変数（フェーズ3機能）

トリガー間で変数を共有：

```json
{
  "id": "p5_count_tracker",
  "type": "cast_start",
  "match": { "cast_id": "0xABCD" },
  "set_variable": { "name": "p5_count", "operation": "increment" }
}
```

```json
{
  "id": "p5_first_safe_call",
  "type": "cast_start",
  "match": { "cast_id": "0xDCBA" },
  "conditions": {
    "all_of": [
      { "variable": "p5_count", "equals": 1 }
    ]
  },
  "actions": [{"type": "tts", "text": "1回目、北東"}]
}
```

操作：`set` / `increment` / `decrement` / `reset`。
変数は戦闘終了時に自動リセット（オプションで永続化可）。

---

## 5. 出力アクション

### 5.1 アクションタイプ一覧

| タイプ | 説明 |
|--------|------|
| `tts` | TTS音声読み上げ |
| `wav` | 事前録音音声ファイル再生 |
| `overlay_text` | 画面中央への大型テキスト表示 |
| `overlay_corner_text` | 画面任意位置への小型テキスト |
| `timer_bar` | タイマーバー表示 |
| `chat_echo` | 自分のチャットウィンドウに出力 |
| `direction_call` | 方角の音声・テキストコール |
| `screen_arrow` | 画面上の方向矢印 |
| `field_marker` | フィールド上のワールド座標マーカー |
| `proximity_feedback` | 安置内/外のフィードバック音 |
| `set_variable` | 状態変数操作（フェーズ3） |
| `chain_trigger` | 別トリガーを連鎖発動（フェーズ3） |

### 5.2 アクションパラメータ

#### `tts`
```json
{
  "type": "tts",
  "text": "外周回避",
  "voice": "ja-JP-Default",
  "rate": 1.0,
  "volume": 0.8,
  "delay": 0
}
```

#### `wav`
```json
{
  "type": "wav",
  "file": "alerts/danger.wav",
  "volume": 0.8,
  "delay": 0
}
```

#### `overlay_text`
```json
{
  "type": "overlay_text",
  "text": "外周回避",
  "duration": 5,
  "color": "#FF0000",
  "size": "large",
  "delay": 0
}
```

#### `timer_bar`
```json
{
  "type": "timer_bar",
  "label": "次の頭割りまで",
  "duration": 15,
  "color": "#FFAA00",
  "warn_at": 5
}
```

#### `direction_call`
```json
{
  "type": "direction_call",
  "safe_zone": "$calculated_safe_zone",
  "format": "cardinal_jp",
  "tts": true,
  "overlay": true
}
```

`format` の選択肢：`cardinal`（N/S/E/W）/ `cardinal_jp`（北/南/東/西）/ `clock`（12時方向）/ `relative_jp`（背面/正面）/ `degrees`（45度）

#### `screen_arrow`
```json
{
  "type": "screen_arrow",
  "from": "self",
  "to": "$calculated_safe_zone",
  "color": "#00FF00",
  "duration": 8
}
```

#### `field_marker`
```json
{
  "type": "field_marker",
  "position": "$calculated_safe_zone",
  "shape": "circle",
  "radius": 2,
  "color": "#00FF00",
  "duration": 8
}
```

#### `proximity_feedback`
```json
{
  "type": "proximity_feedback",
  "safe_zone": "$calculated_safe_zone",
  "tolerance": 2,
  "in_sound": "sounds/safe.wav",
  "out_sound": "sounds/danger.wav",
  "show_distance": true
}
```

### 5.3 変数参照

`$variable_name` 形式で他のトリガーや計算結果を参照可能：

- `$self` / `$party_member_*`：プレイヤー位置
- `$cast_actor` / `$status_actor`：イベント発動元
- `$calculated_safe_zone`：直前の安置計算結果
- `$stored_position_*`：履歴保存された位置

---

## 6. 安置計算プリセット

### 6.1 プリセット一覧（全15種）

| ID | 名称 | 用途 |
|----|------|------|
| `fixed` | 固定方角 | 絶対方角指示 |
| `boss_relative` | ボス相対 | ボス基準の方向 |
| `marker_relative` | マーカー相対 | フィールドマーカー基準 |
| `inverse_of_telegraph` | AoE予兆の反対 | 範囲攻撃の逆側 |
| `find_actor_with_status` | 特定ステータス持ちオブジェクト | バフ/デバフで識別 |
| `find_actor_without_status` | 特定ステータス無しオブジェクト | 安置オブジェクト識別 |
| `find_actor_not_casting` | キャスト中でないオブジェクト | 楽園絶技系 |
| `midpoint` | 複数オブジェクトの中点 | 中間位置 |
| `line_perpendicular` | 線の延長/直交 | 線処理 |
| `between_actors` | 2点間の比率位置 | ノックバック中間など |
| `find_actor_by_distance` | 距離による選択 | 最寄り/最遠 |
| `telegraph_gap` | AoE予兆の隙間 | 激狭安置 |
| `party_member_relative` | PTメンバー基準 | ペア処理 |
| `arena_center_relative` | 戦闘エリア中心相対 | 外周指定 |
| `stored_position` | 履歴ベース | 前ギミック結果参照 |

各プリセットの詳細パラメータは `trigger-schema.json` 参照。

### 6.2 計算結果

各プリセットは以下を返す：

```json
{
  "world_pos": { "x": 100.0, "y": 0.0, "z": 100.0 },
  "from_player": {
    "direction_deg": 45.0,
    "direction_cardinal": "NE",
    "direction_clock": 1.5,
    "direction_relative_to_boss": "back_right",
    "distance": 8.3
  }
}
```

### 6.3 対応形状

ジオメトリ計算で扱う形状：

- 円範囲（中心 + 半径）
- 扇範囲（中心 + 角度範囲 + 半径）
- 直線範囲（始点 + 終点 + 幅）
- ドーナツ範囲（中心 + 内径 + 外径）
- 矩形範囲（中心 + 縦横 + 回転）

`telegraph_gap` プリセットは複数の予兆形状を入力に取り、重ならない領域を計算する。

### 6.4 複合ギミック（AND計算）

複数の安置条件を満たす点を計算：

```json
{
  "method": "intersection",
  "constraints": [
    { "method": "boss_relative", "params": {"angle": 180, "distance": 10} },
    { "method": "inverse_of_telegraph", "params": {"telegraph_source": "$cast_actor"} }
  ]
}
```

`intersection` は全条件を満たす最適点を返す。条件矛盾で解なしの場合は最後の発火条件を採用。

---

## 7. 設定画面（Settings UI）

### 7.1 画面構成

ImGuiベースのウィンドウ。タブ切り替えで以下のページを提供：

1. **コンテンツ一覧**：録画されたコンテンツとトリガー定義の一覧
2. **トリガー編集**：選択したコンテンツのトリガー編集（タイムラインビュー / 集計ビュー切替）
3. **ライブHUD設定**：オーバーレイの位置・サイズ・表示モード
4. **音声設定**：TTS/WAVの音量・出力デバイス・ボイス選択
5. **プロファイル管理**：プロファイルの作成・切替
6. **インポート/エクスポート**：トリガー定義の入出力
7. **全体設定**：プラグイン全体の挙動設定

### 7.2 トリガー編集ページ

#### 7.2.1 タイムラインビュー

横軸に時間、縦に複数レーンでイベントを表示。

- **レーン構成**
  - ボスキャスト
  - 自分のステータス
  - 他PTメンバーのステータス
  - アクション発動
  - フェーズマーカー（HP閾値到達など）

- **イベント表示**
  - 設定済み：緑のブロック
  - 未設定：グレーのブロック
  - 無視済み：薄いグレー（半透明）
  - syncポイント：青枠
  - クリックで編集パネルへ

- **マージ表示**
  - 複数の戦闘ログを統合表示
  - 各イベントに観測回数を表示
  - フィルタ：観測回数N回以上のみ表示
  - マッチングキー：イベント種別 + 識別子 + 相対時刻±許容範囲 + 対象判定

#### 7.2.2 集計ビュー

観測されたイベントをテーブル形式で一覧表示。

| 列 | 内容 |
|------|------|
| 種別 | Cast / Status / Action |
| ID | 16進ID |
| 名前 | アクション名・ステータス名 |
| 発動者 | ソース名 |
| 対象 | 対象種別または付与対象者 |
| 観測回数 | マージ表示での合計 |
| 初回時刻 | 戦闘開始から何秒で初観測 |
| 持続時間 | （Status のみ）デバフ秒数 |
| 設定状態 | 設定済み / 未設定 / 無視済み |

機能：
- フィルタ（種別・状態・名前検索・観測回数）
- ソート（任意の列）
- 行クリックで編集パネル表示
- 一括操作（複数選択して「無視済み」一括設定など）

#### 7.2.3 編集パネル

選択したイベントに対するトリガー定義を編集。

- マッチ条件（ID・名前・対象・持続時間範囲など）
- 複合条件（AND/OR ビルダー）
- アクション一覧（追加・削除・並べ替え）
- 各アクションのパラメータ入力
- 安置計算プリセット選択（位置指示系アクションの場合）
- プレビュー機能（テスト発動）

### 7.3 状態管理

各イベントは3状態を持つ：

| 状態 | 説明 |
|------|------|
| **設定済み** | トリガーが定義されている |
| **未設定** | 観測されているがトリガー未定義 |
| **無視済み** | トリガー化不要として明示的にマークされた |

「無視済み」にすることで、未設定リストから除外できる。理由（reason）を任意で記録可能。

### 7.4 編集機能

- **追加**：未設定イベントをトリガー化
- **編集**：既存トリガーを変更
- **削除**：トリガーを削除（確認ダイアログ + 自動バックアップ）
- **無効化/有効化**：削除せずON/OFF
- **複製**：既存トリガーをコピーして新規作成
- **一括操作**：複数選択での状態変更

### 7.5 インクリメンタル更新

- 既存トリガー定義を保持したまま新規ログをマージ可能
- 新ログに含まれない既存トリガーは「ログなし」状態として残す
- マッチングロジック：イベント種別 + 識別子 + 相対時刻±許容範囲 + 対象判定（許容範囲はデフォルト±2秒、変更可）

---

## 8. ライブHUD（戦闘中オーバーレイ）

### 8.1 ライブタイムライン

戦闘中、画面に表示されるリアルタイムタイムライン。

- **方式**：ハイブリッド（タイムライン駆動 + sync補正）
- **形式**：水平バー型（Cactbot式）
- **デフォルト位置**：画面中央下部
- **位置・サイズ調整**：ドラッグで自由変更、設定画面でリセット可能
- **表示モード切替**：
  - 設定済みのみ
  - 全イベント表示（未設定はグレー）

### 8.2 sync機構

トリガー定義の `sync_points` で指定されたイベントが観測されたら、タイムラインの現在時刻を補正する。

- 早期観測 → タイムラインを進める
- 遅延観測 → タイムラインを停止して待機
- フェーズスキップ判定 → 次のsyncポイントまで早送り

### 8.3 表示要素

- 現在時刻ライン
- 直近の過去イベント（数秒）
- 未来の予告イベント（数十秒）
- アクティブなタイマーバー
- 中央オーバーレイテキスト
- 画面上の方向矢印
- フィールドマーカー（ワールド座標連動）

### 8.4 切り替えコマンド

```
/myplugin timeline all          # 全イベントモード
/myplugin timeline configured   # 設定済みのみ
/myplugin timeline toggle       # 切り替え
/myplugin timeline hide         # 一時非表示
```

---

## 9. ロガー機能

### 9.1 録画制御

#### 9.1.1 自動記録設定

コンテンツ単位で `auto_record` を ON/OFF 設定可能。設定画面のコンテンツページで切替。

#### 9.1.2 手動オーバーライド

スラッシュコマンドで一時的に挙動変更：

```
/myplugin record on    # 記録ON（自動設定を上書き）
/myplugin record off   # 記録OFF
/myplugin record auto  # 自動設定に従う（デフォルト）
```

ゲーム再起動・プラグインリロード時は `auto` にリセット。

### 9.2 出力先

- **ファイル書き出し**（最優先）
  - JSON Lines形式
  - 戦闘単位で1ファイル
  - パス：`recordings/{zone}/{datetime}.jsonl`
  - 戦闘終了時に書き出し（戦闘中はメモリバッファ）

- **ゲーム内チャット出力**
  - デバッグモード時にイベントをチャットエコー
  - 専用チャンネル（システムメッセージ扱い）

- **専用オーバーレイウィンドウ**
  - リアルタイムイベント一覧
  - フィルタ機能（種別・対象）
  - クリックで詳細表示

### 9.3 永久保存

- 録画ログは自動削除しない
- 容量管理オプション：「N日以上前のログを削除」を任意で有効化可能（デフォルト無効）

---

## 10. 音声・出力デバイス

### 10.1 TTSエンジン

- Windows標準のSAPI5（System.Speech.Synthesis）を使用
- 日本語クライアント前提のため、日本語ボイスを推奨
- 将来的に外部TTSエンジン対応の拡張ポイントを残す

### 10.2 音量制御（3階層）

| 階層 | 用途 |
|------|------|
| マスター音量 | 全音声の総量 |
| カテゴリ別音量 | TTS / WAV / フィードバック音 個別 |
| トリガー個別音量 | 各トリガーで上書き |

### 10.3 出力デバイス

- デフォルト：システムデフォルトデバイス
- 設定で出力デバイス指定可能（ヘッドフォン / スピーカー切り替えなど）
- ゲーム音声と独立して動作（別チャンネル）

---

## 11. パフォーマンス目標

- **フレーム影響**：1フレームあたり 1ms 未満を目標
- **メモリ使用量**：常駐 100MB 以下
- **重い処理は別スレッド**：ジオメトリ計算、ファイルI/O
- **ObjectTable走査**：必要時のみ、ObjectIDキャッシュで重複処理を回避
- **ImGui描画**：表示中のレーンのみ描画（仮想スクロール）
- **ログ書き出し**：メモリバッファ + 一定間隔フラッシュ + 戦闘終了時の最終フラッシュ

---

## 12. インポート/エクスポート

### 12.1 エクスポート

- 単一コンテンツのトリガー定義をJSONファイル単体として出力
- メタ情報埋め込み（作者名・バージョン・作成日時・コメント）
- フォーマットバージョン記録（後方互換のため）

### 12.2 インポート

- JSONファイルを読み込み、既存トリガーと突き合わせ
- コンフリクト時の選択肢
  - 上書き（既存を消す）
  - マージ（IDが重複しないものだけ追加）
  - スキップ（インポートしない）
- インポート前に自動バックアップ

### 12.3 フォーマットバージョン

- トリガー定義に `version` フィールド
- 古いバージョンのファイルを読み込んだ場合は自動マイグレーション
- 互換性のない変更時は警告

---

## 13. プラグイン設定

### 13.1 全体設定（config.json）

```json
{
  "language": "ja",
  "active_profile": "default",
  "default_voice": "ja-JP-Default",
  "master_volume": 0.8,
  "tts_volume": 1.0,
  "wav_volume": 1.0,
  "feedback_volume": 0.8,
  "audio_device": null,
  "merge_tolerance_seconds": 2.0,
  "log_retention_days": null,
  "debug_mode": false
}
```

### 13.2 コンテンツ別自動設定

各コンテンツのトリガー定義ファイルに含める：

```json
"auto_settings": {
  "enable_triggers": true,
  "auto_record": false,
  "show_timeline": true
}
```

すべてデフォルトOFF。明示的にONにしたコンテンツのみ自動動作。

### 13.3 スラッシュコマンド

```
/myplugin                       # 設定画面を開く
/myplugin record on/off/auto    # 記録モード制御
/myplugin timeline <mode>       # タイムライン表示制御
/myplugin profile <name>        # プロファイル切替
/myplugin reload                # トリガー再読込
/myplugin debug on/off          # デバッグモード切替
```

詳細なコマンド体系は実装時に確定。

---

## 14. 実装ロードマップ

詳細は `roadmap.md` を参照。

### MVP
基本的な検知・出力・録画・編集機能。安置計算プリセットの基本パターン。

### フェーズ2
位置情報系（画面矢印・フィールドマーカー）、安置計算プリセット全15種、複合ギミック対応、デバフ秒数分岐。

### フェーズ3
状態変数・条件分岐・連鎖トリガー・カスタムスクリプト。

---

## 15. 制約事項・注意点

### 15.1 規約上の注意

本プラグインはFFXIVの規約に違反しない範囲（情報表示・読み上げ）に機能を限定する。自動操作系の機能は実装しない。Square Enixの方針変更により利用が制限される可能性は常にある。

### 15.2 Dalamud依存

Dalamud APIの仕様変更により、パッチアップデート時にプラグインが動作しなくなる可能性がある。Dalamud本体のアップデート完了後にプラグイン側も追従する必要がある。

### 15.3 自己責任

本プラグインは個人用ツールとして開発される。利用は自己責任。

---

## 16. 用語集

| 用語 | 説明 |
|------|------|
| トリガー | 特定のイベント検知に対する反応定義 |
| アクション | トリガー発動時に実行される反応（読み上げ・表示など） |
| プリセット | 安置計算など、よく使うパターンを定義した計算ロジック |
| プロファイル | トリガーセットの切替単位（ジョブ別など） |
| sync ポイント | ライブタイムラインの時刻補正に使う基準イベント |
| マージ表示 | 複数戦闘ログを統合してタイムラインに表示する機能 |
| インクリメンタル更新 | 既存トリガーを残しつつ新規ログから追加で設定する操作 |

---

## 付録A: 関連ドキュメント

- `trigger-schema.json` — トリガー定義のJSONスキーマ
- `trigger-samples/` — 極エヌオー想定のサンプルトリガー
- `roadmap.md` — 実装フェーズ別のロードマップ
