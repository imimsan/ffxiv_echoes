# FFXIV Echoes — 現在の仕様

最終更新: 2026-05-06。
ブランチ: `feature/minimap-boss-roles`（develop に未マージの作業ブランチ）。
DLL: `src/FfxivEchoes/bin/x64/Debug/FfxivEchoes.dll`

このドキュメントは「今動いている機能」を上から下まで列挙したもの。最初の `SPEC.md` は構想段階で、ここまでの実装で内容が変動した部分を補足する位置づけ。

---

## 1. 全体像

「録画 → トリガー化 → 戦闘中に音声・視覚で支援」の循環を回すプラグイン。

```
┌────────────────────────────────────────────────────────────────┐
│ プレイヤー: ボスに突入 → /echoes record on で録画              │
│  ↓                                                              │
│ Plugin: イベントを JSONL に保存（cast_id / 時刻 / 発動者 等）  │
│  ↓                                                              │
│ プレイヤー: コンテンツ一覧 → 編集 → 「✨ 録画から自動生成」    │
│  ↓                                                              │
│ Plugin: Lumina の Action データを読んで AoE 形状を判定         │
│         → TTS + ミニマップ ギミック + フィールド円の           │
│         トリガー一式を一発生成                                  │
│  ↓                                                              │
│ プレイヤー: 同コンテンツに再突入                                │
│  ↓                                                              │
│ Plugin:                                                          │
│  - 予測時刻の N 秒前にミニマップに「次：〇〇」を表示            │
│  - キャスト開始で「確定：〇〇」に切り替わり、フィールド円も追加│
│  - 同期オフセットで実時刻ズレを自動補正                         │
│  - 自分・PT・他の敵 をミニマップ上にプロット                    │
└────────────────────────────────────────────────────────────────┘
```

---

## 2. 起動フロー（Dalamud dev plugin）

1. `/xlsettings` →「Experimental」→ Dev Plugin Locations に
   `C:\...\src\FfxivEchoes\bin\x64\Debug\FfxivEchoes.dll` を追加
2. `/xlplugins` → Dev Tools → FFXIV Echoes → Enable
3. `/echoes` でメインウィンドウが開く（最初は「使い方」タブ）

**プラグインの初期ゾーン取得**: ロード時に既にゾーン内にいる場合、`ZoneCapture.PublishInitialState()` で初期 ZoneChangedEvent を発行する。これがないと TriggerEngine が `_currentZone="Unknown"` のままトリガーが無反応になる（過去のバグ修正済）。

---

## 3. メインウィンドウ（`/echoes`）のタブ構成

| タブ | 役割 |
|---|---|
| **使い方** | 初回利用ガイド・コマンド一覧・トラブルシュート（HelpTab） |
| **コンテンツ一覧** | ゾーン一覧。新規作成 / 編集 / 削除。録画ありゾーンも表示 |
| **トリガー編集** | サブタブ：トリガー一覧 / 集計（観測イベント） / ファイル設定 / ノート / バックアップ |
| **ライブイベント** | キャプチャ済みイベントを直近 300 件リアルタイム表示・カテゴリ別フィルタ |
| **音声** | TTS / WAV のデバイス選択とボリューム |
| **プロファイル** | コンテンツ別にトリガー有効/無効を切替 |
| **入出力** | トリガー定義のインポート/エクスポート |
| **全体設定** | 全体オプション（DebugMode / EchoTriggerFires / AutoVisualForTts 等） |

---

## 4. ライブ HUD（戦闘中に画面に出る要素）

戦闘中・ファイル設定の `show_timeline = true` で自動表示。すべて drag 可能。

### 4.1 LiveTimelineWindow （水平タイムライン）

- 中央が現在時刻、左 10 秒（過去）、右 30 秒（未来）
- 過去側：実発火した cast / trigger を点付きラベルで表示
- 未来側：録画 aggregate にあった cast を点線 + ラベル ピルで予測表示（同期オフセット適用済）
- 各イベントを最大 4 行に縦スタックして重なり回避
- ラベルは 14 文字でトランケート、半透明背景ピルで重なっても読める
- 右上に `MODE` と `SYNC: +X.Xs rec:〇〇` を表示

### 4.2 MinimapWindow （俯瞰アリーナ図）

- ギミック発生時のみポップアップ。デモの右パネル相当
- gimmick タイプ: `outer_ring` / `inner_circle` / `scatter` / `stack` / `cone`
- 各 gimmick で固有の描画パターン（中央安置 / 外周安置 / 4方向散開 / 中央集合 / 扇形）
- ボス（中央）+ 自分（明緑 + 白リング）+ PT メンバー（ロール別色） + 他の敵（赤ドット）を同時にプロット
- cone はボスの **実際の Rotation** を読んで方向を決定
- safe_zone（F4-F7 の SafeZoneCalculation）が指定されていれば緑の点線円で重畳描画
- callout テキスト + 残り秒数を画面下部に表示

### 4.3 UpcomingEventsWindow （次に来るイベント縦リスト）

- 録画予測 cast + ノートを統合して時系列順に並べる（最大 6 件）
- 各行: `アイコン / 残り秒数 / ラベル / sub`
- 残り 5 秒以内は背景 amber + 文字色強調（imminent）
- border-left 4px がイベント種別色（青=予測 / 緑=ノート）
- アイコンはキャスト名から推測（肥大→💥、追跡→🎯、ウィング→🪽 等）
- 同期オフセット適用済

### 4.4 WorldOverlayWindow （地面に描画）

- `field_marker` アクション、AutoTelegraph、`screen_arrow` 等の出力先
- 円・四角・×印・矢印を **ワールド座標**で描画（カメラ追従）
- 半径は `WorldToScreen` で実距離 → 画素変換（28m AoE は実際に画面で 28m 相当のサイズで見える）

### 4.5 OverlayWindow （中央テキスト + タイマーバー）

- `overlay_text` / `timer_bar` アクションの出力先
- 画面中央にフェードイン/アウトで大型テキスト
- 右下にカウントダウンバー（warn 残時間以下でパルス）

---

## 5. トリガー定義の構造（`triggers/<zone>.json`）

```jsonc
{
  "version": "1.0",
  "zone": "雲廊",
  "auto_settings": {
    "enable_triggers": true,                  // 突入時に自動有効化
    "auto_record": false,                     // 突入時に自動録画開始
    "show_timeline": true,                    // ライブ HUD を表示
    "show_predicted_casts": true,             // タイムラインに予測描画
    "show_auto_telegraphs": true,             // 敵 AoE を地面に自動描画
    "predict_advance_warning_sec": 10         // 予測キャスト N 秒前に通知
  },
  "sync_points": [
    { "id": "phase2", "type": "cast_start", "cast_id": "0xPHASE2", "expected_time": 180 }
  ],
  "triggers": [
    {
      "id": "muno_higai",
      "name": "無の肥大（外周回避）",
      "enabled": true,
      "type": "cast_start",
      "match": { "cast_id": "0x9D32", "cast_name": "無の肥大", "source": "ボス名" },
      "actions": [
        { "type": "tts", "text": "外周回避" },
        { "type": "arena_view", "gimmick": "outer_ring", "callout": "中央安置", "duration": 5 }
      ]
    }
  ],
  "notes": [
    {
      "id": "first_mit",
      "label": "ランパート + ブラインド",
      "icon": ["🛡", "💨"],
      "role": "tank",
      "advance_warning_sec": 3,
      "color": "#FBBF24",
      "attached_to": { "cast_id": "0x9D32" }   // ← 時刻を録画に紐付け（任意）
    }
  ]
}
```

---

## 6. アクション種類

| type | UI 名 | 効果 |
|---|---|---|
| `tts` | 音声読み上げ | SAPI5 で text を読み上げ |
| `wav` | WAV 再生 | NAudio で file を再生 |
| `overlay_text` | 中央テキスト | 画面中央に大型テキストを duration 秒表示 |
| `overlay_corner_text` | コーナーテキスト | 画面端に控えめ表示（実装簡略） |
| `timer_bar` | タイマーバー | 画面右下にカウントダウン |
| `chat_echo` | チャット出力 | 自分のチャットに text を出力 |
| `direction_call` | 方位読み上げ | 計算した安置の方向を TTS（cardinal/clock/relative_jp 等） |
| `screen_arrow` | 矢印表示 | 安置への矢印を画面上に描画 |
| `field_marker` | フィールド円 | 地面に円・四角・×・矢印を描画 |
| `proximity_feedback` | 距離音 | 安置との距離をビーコン音で表現 |
| `set_variable` | 変数設定 | 状態変数を増減（trigger.set_variable で書く） |
| `chain_trigger` | 連鎖発動 | 別 trigger_id を発動 |
| `store_position` | 位置保存 | 現在位置を名前付きで保存（後で stored_position から参照） |
| `arena_view` | 俯瞰アリーナ図 | MinimapWindow に gimmick 表示 |

---

## 7. SafeZone 計算プリセット（16 種、F4-F7）

`safe_zone.method` で指定。各 method の `params` でパラメータ。

| method | 概要 |
|---|---|
| `fixed` | 固定座標 |
| `boss_relative` | ボスからの相対 |
| `marker_relative` | フィールドマーカー A/B/C/D 基準 |
| `arena_center_relative` | アリーナ中心からの相対 |
| `inverse_of_telegraph` | AoE の反対側（最も汎用） |
| `party_member_relative` | PT メンバー基準 |
| `find_actor_with_status` | 特定ステータスを持つ敵基準 |
| `find_actor_without_status` | 特定ステータスを持たない敵基準 |
| `find_actor_not_casting` | キャストしていない敵基準 |
| `find_actor_by_distance` | 自分から最近 / 最遠の敵基準 |
| `midpoint` | 2 アクターの中点 |
| `between_actors` | 2 アクター間の指定割合 |
| `line_perpendicular` | 2 点を結ぶ線への垂線 |
| `telegraph_gap` | 複数 AoE の隙間 |
| `intersection` | 複数計算の交差点（AND） |
| `stored_position` | 以前 store_position で保存した位置 |

トリガー編集 UI に method ドロップダウン + params の生 JSON 入力欄。

---

## 8. 自動化サービス（バックグラウンド）

戦闘中に常駐して動く解析器群。

### 8.1 PredictedCastReminderService
- `auto_settings.predict_advance_warning_sec > 0` で有効化
- CombatStarted で録画 aggregate を読み、各 cast_start を予測としてスケジュール
- 予測時刻の N 秒前に：
  - TTS「次、〇〇」
  - MinimapWindow に「次：〇〇」のミニマップ ポップアップ（gimmick 自動推測）
- 同じ cast_id を扱う既存 trigger があればスキップ（重複排除）

### 8.2 AutoTelegraphService
- `auto_settings.show_auto_telegraphs = true` で有効化
- 敵キャスト開始時に Lumina Action の `EffectRange` / `CastType` を読んで：
  - WorldOverlay にフィールド円（実半径）を描画
  - MinimapWindow にも「確定：〇〇」のミニマップ表示
  - cone の場合はボスの Rotation で方向決定
- EffectRange > 50m はスキップ（誤データ対策）
- PC（プレイヤー）のキャストは無視

### 8.3 NoteReminderService
- ノートの `advance_warning_sec` に従い先行 TTS 通知
- `attached_to` 指定があれば録画 aggregate から時刻解決
- 同期オフセット適用済

### 8.4 SyncOffsetTracker
- 録画予測 vs 実戦の時刻ズレを自動追跡
- 録画で t=180s だった cast が実戦で t=192s なら offset = +12s
- `LiveTimelineWindow` / `PredictedCastReminderService` / `NoteReminderService` 全てが時刻表示・通知に offset を加算
- 30 秒以上の急ジャンプは誤検知扱いで破棄

### 8.5 TriggerAutoGenerator
- 「✨ 録画から自動生成」ボタンの裏側
- 録画 aggregate の各 cast_start に対し：
  - Lumina で AoE 形状判定 → CastType に応じたアクション組合せを生成
    - CastType=2/5 + 範囲 < 15m → TTS + field_marker(boss_relative)
    - CastType=2/5 + 範囲 ≥ 25m → 上記 + arena_view "outer_ring"
    - CastType=6 (Donut) → arena_view "inner_circle"
    - CastType=3/4 (Cone/Line) → arena_view "cone"
- PT 内のキャストはスキップ
- 既存 trigger と cast_id 重複ならスキップ
- 生成プレビュー modal で確認 → 「N 件を作業コピーに追加」

---

## 9. 録画 / 集計

- `/echoes record on/off/auto` で録画モード切替
- 戦闘開始で `pluginConfigs/FfxivEchoes/recordings/<zone>/<datetime>.jsonl` に追記
- 各行は 1 イベント（cast_start / cast_complete / status_gain / hp_change / object_appear / ...）
- 1 行目に meta（zone / start_time / party 名簿）
- `RecordingScanner.Aggregate(zone)` でゾーン全録画を集計（cast_id ごとの初回時刻と回数）
- 「集計（観測イベント）」タブでタイムライン or 表で閲覧
- 「自分・PT のイベントを隠す」フィルタ（録画 meta の party 名と照合）
- 「このイベントからトリガー作成」で 1-click 化

---

## 10. ノート（軽減・LB 等のメモ）

- `time` 直接指定 OR `attached_to: MatchCondition` で録画キャストに紐付け
- 紐付けノートは録画 aggregate から時刻解決（自動追従）
- 「📎 紐付け」ボタンで cast 選択 popup
- 「録画キャストから一括追加」で複数 cast に対しまとめて生成
- LiveTimeline と UpcomingEventsWindow に表示
- `advance_warning_sec` で TTS 先行通知

---

## 11. プロファイル / インポート

- `pluginConfigs/FfxivEchoes/profiles/<name>.json` で zone × trigger_id の有効/無効を保持
- 同コンテンツでも難易度・PT 構成で使い分けられる
- TriggerExportImport: zone を JSON 単体で書き出し / 取り込み（コンフリクト解決つき）

---

## 12. 全体設定（Configuration）

| 項目 | 既定 | 効果 |
|---|---|---|
| `Language` | "ja" | 表示言語（日本語固定） |
| `DebugMode` | false | 全イベントをチャットに出力 |
| `EchoTriggerFires` | true | 発火を `[Echoes] ⚡ Trigger:` で chat 出力 |
| `AutoVisualForTts` | false | TTS のみのトリガーに自動 overlay_text 追加 |
| `MasterVolume` | 0.8 | TTS / WAV / Feedback の上位ゲイン |
| `TtsVolume` | 1.0 | TTS 個別ゲイン |
| `WavVolume` | 1.0 | WAV 個別ゲイン |
| `FeedbackVolume` | 0.8 | proximity_feedback ゲイン |
| `AudioDevice` | null | 出力デバイス（NAudio で列挙） |
| `MergeToleranceSeconds` | 2.0 | 録画マージ判定の許容秒数 |
| `LogRetentionDays` | null | 録画ログの自動削除日数 |

---

## 13. コマンド一覧

| コマンド | 効果 |
|---|---|
| `/echoes` | 設定画面を開く（最初は「使い方」タブ） |
| `/echoes record <on/off/auto>` | 録画モード切替 |
| `/echoes debug <on/off>` | デバッグモード切替 |
| `/echoes timeline <all/configured/hidden>` | LiveTimeline 表示モード切替 |
| `/echoes reload` | トリガー定義を再読込 |
| `/echoes profile <activate/list/...>` | プロファイル操作 |
| `/echoes help` | コマンド一覧 |

---

## 14. JSON 値と UI 表示の対応（i18n）

JSON 値は英語キーで保つ（互換性のため）。UI ドロップダウンのみ日本語化：
- gimmick: `outer_ring` → 「外周回避（中央安置）」
- direction: `N` → 「北 (N)」
- safe_zone method: `inverse_of_telegraph` → 「AoE の反対側」
- action type: `tts` → 「音声読み上げ (TTS)」
- 等々（`Localization.cs` で集約）

---

## 15. 未実装 / 既知の制約

- Cone / Line の AoE 形状は cone gimmick で代替表現（具体的な扇形角度は arena_view の `fan_deg` で手動指定）
- フィールドマーカー（A/B/C/D ウェイマーク）の位置取得は未実装（マーカー基準の SafeZone は手動座標指定が必要）
- AutoTelegraph は CastType=2/5/3/4/6 のみ対応。それ以外（特殊形状）はスキップ
- カータライズ等で Lumina の `EffectRange` が異常値（80m 以上）の場合はスキップ（誤動作防止）
- `set_variable` / `store_position` のアクション編集 UI は最小限（JSON 直編集推奨）
- 多人数 PT（8 人）でロール解決失敗時は灰色ドット フォールバック

---

## 16. ディレクトリ構成

```
src/FfxivEchoes/
├── Plugin.cs                          # エントリポイント
├── Configuration.cs                   # 全体設定
├── Capture/                           # M3: イベントキャプチャ
│   ├── CombatClock.cs / CombatStateCapture.cs
│   ├── ZoneCapture.cs / CastCapture.cs / StatusCapture.cs
│   └── HpCapture.cs / ObjectCapture.cs
├── Events/                            # GameEvents POCOs + EventBus
├── Recording/                         # M4: ロガー + 集計
│   ├── BattleRecorder.cs
│   ├── RecordingController.cs
│   └── RecordingScanner.cs            # 集計 + party 名抽出
├── Triggers/                          # M5+: トリガー基盤
│   ├── TriggerStore.cs / TriggerLoader.cs / TriggerWatcher.cs
│   ├── TriggerEngine.cs
│   ├── Matching/                      # EventMatcher / ConditionEvaluator / TargetResolver
│   ├── Models/                        # JSON ↔ POCO
│   ├── NoteReminderService.cs
│   ├── PredictedCastReminderService.cs
│   ├── AutoTelegraphService.cs
│   ├── SyncOffsetTracker.cs
│   ├── TimelineNoteResolver.cs
│   ├── AoeResolver.cs                 # Lumina Action から AoE 形状を判定
│   └── TriggerAutoGenerator.cs        # 「✨ 録画から自動生成」のロジック
├── Actions/                           # M7: アクションディスパッチ
│   ├── ActionDispatcher.cs
│   └── Handlers/                      # 14 種のアクションハンドラ
├── SafeZone/                          # F4-F7: 16 種プリセット
├── Profiles/                          # F10
├── Variables/                         # P1+P2: 状態変数
├── Scripting/                         # P5: カスタムスクリプト（スタブ）
├── Diagnostics/                       # DebugChatEcho
├── Commands/                          # /echoes 系
└── Windows/                           # ImGui ウィンドウ群
    ├── MainWindow.cs                  # タブホスト
    ├── OverlayWindow.cs               # 中央テキスト + タイマーバー
    ├── LiveTimelineWindow.cs          # 水平タイムライン
    ├── MinimapWindow.cs               # 俯瞰アリーナ図
    ├── UpcomingEventsWindow.cs        # 次に来るイベント
    ├── WorldOverlayWindow.cs          # 地面 / 矢印描画
    └── Tabs/
        ├── HelpTab.cs                 # 使い方
        ├── ContentListTab.cs          # ゾーン一覧 + 新規/削除
        ├── TriggerEditorTab.cs        # 編集 UI（5 サブタブ）
        ├── LiveHudTab.cs              # ライブイベント表示
        ├── AudioTab.cs / ProfileTab.cs / ImportExportTab.cs / GeneralSettingsTab.cs
        ├── TimelineRenderer.cs        # 集計タブの水平ビュー
        └── Localization.cs            # 英→日表示マップ
```

---

## 17. データ保存パス

```
%AppData%\XIVLauncher\pluginConfigs\FfxivEchoes\
├── config.json                   # 全体設定
├── triggers/<zone>.json          # ゾーン別トリガー定義
├── triggers_backup/<zone>/...    # 編集時の自動バックアップ
├── recordings/<zone>/...jsonl    # 録画ログ
├── profiles/<name>.json          # プロファイル
└── scripts/                      # カスタムスクリプト（P5、現在スタブ）
```

---

## 18. ビルド / 配布

- ターゲット: Dalamud.NET.Sdk 15.0.0 / .NET 8+
- ビルド: `dotnet build src/FfxivEchoes/FfxivEchoes.csproj -c Debug -p:Platform=x64`
- 出力: `src/FfxivEchoes/bin/x64/Debug/FfxivEchoes.dll` + `FfxivEchoes.json`（マニフェスト）
- 開発ロード: Dalamud Dev Plugin Locations に DLL のフルパスを登録

---

## 19. 主要な設計判断

- **JSON 値は英語キー固定**: 互換性とコピペ可搬性のため。UI だけ日本語化
- **同期オフセットは加算方式**: 全コンポーネントで `predicted + offset` を見るだけで追従できる単純さを優先
- **AutoVisualForTts は OFF 既定**: 中央テキストは画面が埋まりやすいため、明示設定したトリガーのみ表示
- **AutoTelegraph は ObjectTable 直読**: 録画には位置情報を載せていないため、実戦時のみ有効
- **PT 内キャストは予測対象外**: 自分の使ったスキルで通知が鳴るのを防ぐ
- **EffectRange > 50m はスキップ**: Lumina データの異常値（特殊アクション）対策

---

## 20. 既知のバグ / 未解決事項

- ヘイトウィング等で source 位置が `(0, 0)` と返る場合があり、フィールド円が原点付近に出る → 環境再現確認中
- Lumina の `CastType` が一部のキャストで期待と違うケース（要追加検証）
- `set_variable` アクションのフォーム編集 UI が未実装（trigger 単位の `set_variable` 設定項目で代替）
- `attached_to` で名前のみ指定したノートは録画に同名 cast がないと解決失敗（cast_id 指定推奨）
