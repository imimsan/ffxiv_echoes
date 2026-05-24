# Cast → Object 出現予測 — 設計書

## 1. 目的とスコープ

「**特定の cast 後に N 秒後で Object が出現し、その object が即時 AoE を発動する**」パターンを録画から自動学習し、cast 検知時点で **事前に AoE 予告を描画する** 汎用機能。

### 想定される適用パターン（汎用）

| パターン | 例（月の底ベース） |
|---|---|
| 召喚系 cast → 数秒後に add が固定位置で出現 → 即時 AoE | パラデイグマ → ケツアクアトル ×4 が四隅出現 → ドーナツ AoE |
| 召喚系 cast → ボス本体の動作 → object 出現 | ステュクス cast 系 |
| HP%トリガー → object 出現 → 床塗り展開 | フェーズ移行時の add 召喚 |

「コンテンツ固有のハードコード」を一切書かず、**録画 1〜2 回で自動学習**して次回戦闘から動作する。

### スコープ外

- ランダムターゲット型 AoE（プレイヤー個人 marker）の予測
- AI 推定（位置がランダムなギミックを推測する機能）
- Splatoon の packet hook 系 cast snapshot

### 設計上の核心

**Dalamud の ObjectTable に actor が登録されるのを待たない。録画から学習した「N 秒後に出現する」予測時刻と「学習済み位置」で予告描画する。** これによって ObjectTable 登録遅延（月の底のパラデイグマだと最大 11 秒）を回避できる。

## 2. データモデル

### 新規型: `PredictedObjectSpawn`

`StrategyProfile.PredictedObjectSpawns` に追加（既存の `ObjectAoeRules` と並列）。

```csharp
public sealed class PredictedObjectSpawn
{
    public string Id { get; set; }                  // 一意 ID (cast_id + object_name + position hash)
    public bool Enabled { get; set; } = true;

    // 起点となる cast
    public string TriggerCastId { get; set; }       // 例: "0x67BF"
    public string TriggerCastName { get; set; }     // 例: "パラデイグマ"
    public string? TriggerSourceName { get; set; }  // 例: "ゾディアーク" (任意)

    // タイミング
    public string TriggerEvent { get; set; } = "cast_start";  // "cast_start" or "cast_complete"
    public double DelaySec { get; set; }            // cast から object 出現までの平均遅延
    public double DelaySecJitter { get; set; }      // 観測ばらつき (標準偏差)

    // 出現する object
    public string ObjectName { get; set; }          // 例: "ケツアクアトル"
    public uint? ObjectDataId { get; set; }         // 例: 14388
    public int ObservedSpawnCount { get; set; }     // 例: 4 (同時出現体数の最頻値)

    // 位置 (アリーナ中心相対座標で記録)
    public List<SpawnPoint> Positions { get; set; } // 4 体なら 4 つの相対座標
    public double PositionVariance { get; set; }    // 位置のばらつき
    public bool IsPositionStable { get; set; }      // ばらつき < しきい値なら true

    // AoE 形状（学習済み or ObjectAoeRule への参照）
    public string Shape { get; set; }               // "donut" / "circle" / "rect" 等
    public double RadiusM { get; set; }
    public double? InnerRadiusM { get; set; }
    public double? FanDeg { get; set; }
    public double? HalfWidthM { get; set; }
    public double DurationSec { get; set; } = 14.0;

    // 信頼度
    public int ObservedFileCount { get; set; }      // 何ファイルの録画で観測されたか
    public int ObservedTotalCount { get; set; }     // 全観測回数
    public double Confidence { get; set; }          // 0.0–1.0 信頼度 (UI 表示用)

    // メタデータ
    public string Source { get; set; }              // "recording" / "manual" / "merged"
    public DateTimeOffset LearnedAt { get; set; }
    public DateTimeOffset? LastObservedAt { get; set; }
}

public sealed class SpawnPoint
{
    public double X { get; set; }   // アリーナ中心相対座標
    public double Z { get; set; }
}
```

### `StrategyProfile` への追加

```csharp
[JsonPropertyName("predicted_object_spawns")]
public List<PredictedObjectSpawn> PredictedObjectSpawns { get; set; } = new();
```

### JSON 例（月の底パラデイグマの場合）

```json
{
  "id": "spawn_67BF_14388",
  "enabled": true,
  "trigger_cast_id": "0x67BF",
  "trigger_cast_name": "パラデイグマ",
  "trigger_source_name": "ゾディアーク",
  "trigger_event": "cast_start",
  "delay_sec": 14.92,
  "delay_sec_jitter": 0.15,
  "object_name": "ケツアクアトル",
  "object_data_id": 14388,
  "observed_spawn_count": 4,
  "positions": [
    {"x": -11.21, "z": -12.64},
    {"x":   9.79, "z": -12.64},
    {"x": -11.21, "z":   8.36},
    {"x":   9.79, "z":   8.36}
  ],
  "position_variance": 0.02,
  "is_position_stable": true,
  "shape": "donut",
  "radius_m": 6.0,
  "inner_radius_m": 2.0,
  "duration_sec": 14.0,
  "observed_file_count": 3,
  "observed_total_count": 12,
  "confidence": 0.95,
  "source": "recording",
  "learned_at": "2026-05-15T...",
  "last_observed_at": "2026-05-15T..."
}
```

## 3. 学習パイプライン

### 3.1 録画スキャン拡張 (`RecordingScanner`)

新規メソッド `IEnumerable<CastObjectPair> ExtractCastObjectPairs(string zone)`:

入力：録画 jsonl ファイル群
出力：`(CastEvent, ObjectAppearGroup, DelaySec)` のペアリスト

アルゴリズム：
1. 各録画ファイルを time 順にイベント取得
2. `cast_complete` (or `cast_start`、設定) を走査
3. cast 発生時刻から **30 秒以内** に `object_appear` を集める
4. **同時刻 (誤差 < 0.5 秒) の object_appear を「同時出現グループ」** としてまとめる
5. グループ内の object は **同一 ObjectName** で絞る
6. 1 cast に対して最も近い同時出現グループを 1 ペアとして返す（複数候補なら最も観測回数が多い）

### 3.2 学習器 (`PredictedObjectSpawnLearner`)

新規クラス。`AggregatedEvents` を入力に、`PredictedObjectSpawn` リストを出力。

ロジック：
1. すべての録画ファイルから `CastObjectPair` を抽出
2. `(TriggerCastId, ObjectName, ObjectDataId)` ごとにグループ化
3. 各グループで：
   - 遅延の平均と標準偏差を計算
   - 出現位置（アリーナ中心相対座標）を全観測点から取得
   - 位置の **クラスタリング**（k-means や DBSCAN 的に）で n 体パターンを抽出
   - 各クラスタの中心座標を `SpawnPoint` として保存
   - クラスタ内ばらつきが小さければ `IsPositionStable=true`
   - 同名で AoE 形状が `object_aoe_rules` にあれば参照、なければ Lumina から推定
4. **信頼度フィルタ**：観測回数 >= 2 && ファイル数 >= 2 のみ保持
5. 既存の `predicted_object_spawns` とマージ（手動編集分は上書きしない）
6. `TriggerStore.SaveZone` で永続化

### 3.3 学習トリガー

A. 録画完了時（`BattleRecorder.OnCombatEnd`）に自動学習
B. ユーザーがコマンド `/echoes learn-spawns [zone]` を打つ
C. プラグイン起動時に既存録画から一括学習

## 4. 発火パイプライン

### 4.1 新規サービス: `PredictedObjectSpawnService`

責務：cast 検知時に該当する `PredictedObjectSpawn` を発火する。

依存：`TriggerStore`, `IFramework`, `MinimapWindow`, `ActorTrackedAoeService`, `IEventBus`

```csharp
private readonly IDisposable _castStartSub;
private readonly IDisposable _castCompleteSub;
private readonly IDisposable _objectAppearSub;
private readonly List<ScheduledSpawn> _scheduledSpawns = new();

private void OnCastStart(CastStartedEvent ev)
{
    foreach (var spawn in GetMatchingSpawns(ev, "cast_start"))
        ScheduleSpawn(spawn, ev);
}

private void OnCastComplete(CastCompletedEvent ev)
{
    foreach (var spawn in GetMatchingSpawns(ev, "cast_complete"))
        ScheduleSpawn(spawn, ev);
}

private void ScheduleSpawn(PredictedObjectSpawn spawn, IGameEvent sourceEvent)
{
    // 即時に予告 AoE を MinimapWindow + ActorTrackedAoeService に登録
    // 各 Positions に対応する zone を 4 つ作って ActionDefinition.AoeZones で発火
    // 各 zone は:
    //   Shape = spawn.Shape
    //   X, Z = lockedCenter + spawn.Position
    //   RadiusM = spawn.RadiusM
    //   DurationSec = spawn.DelaySec + spawn.DurationSec + マージン
    //   Color = "#FFA500" (予告色、薄いオレンジ)  ← 確定描画と区別
    //   Anchor = "static"
    //   LiveFloorPaint = true
    //   IsDanger = true

    var action = new ActionDefinition {
        Type = "arena_view",
        Callout = $"予告: {spawn.ObjectName} ×{spawn.ObservedSpawnCount}",
        Duration = spawn.DelaySec + spawn.DurationSec,
        AoeZones = BuildZonesFromPositions(spawn),
        ArenaCenterX = arena.LockedArenaCenter?.X,
        ArenaCenterZ = arena.LockedArenaCenter?.Z,
    };

    _bus.Publish(new TriggerFiredEvent(
        Timestamp: DateTimeOffset.UtcNow,
        Zone: _currentZone,
        TriggerId: $"__predicted_spawn_{spawn.Id}",
        TriggerName: $"予告: {spawn.ObjectName}",
        Actions: new[] { action },
        SourceEvent: sourceEvent));

    _scheduledSpawns.Add(new ScheduledSpawn(spawn, sourceEvent, DateTimeOffset.UtcNow));
}

private void OnObjectAppeared(ObjectAppearedEvent ev)
{
    // 予告中の spawn のうち、object が実際に出現したものを「確定」に格上げ
    // 実位置と予告位置を比較し、差分があれば zone を補正（または別 zone で確定描画）
    // 既存の AddObjectAoeService.FireGroup 経路と二重にならないよう dedup
}
```

### 4.2 既存サービスとの統合

- **`MechanicTriggerService.OnCastStart`** はそのまま動く（mechanic 経由の発火）。`PredictedObjectSpawnService` は独立して並列に動く
- **`AddObjectAoeService`** は ObjectAppearedEvent 経由の確定描画を継続。`PredictedObjectSpawnService` で先に予告された場合、実位置との差分があれば既存 zone を補正、なければそのまま「確定」に格上げ（既存の SuppressAutoLuminaForCast 相当の dedup を再利用）
- **`PredictedCastReminderService`** はそのまま（TTS / overlay_text 警告のみ）

### 4.3 描画の二重発火防止

`PredictedObjectSpawnService` が発火した予告 zone と `AddObjectAoeService.FireGroup` の実位置 zone が二重描画されないように：

A. 予告 zone は **アンカー固定の静的描画**（actor 追跡しない）
B. 実 object 出現を検知したら、予告 zone を「実位置で更新」する（または予告を消して AddObjectAoeService に任せる）
C. 既存の `MinimapWindow.SuppressAutoLuminaForCast` パターンを参考に、予告と確定の dedup を実装

## 5. UI 拡張 (`TriggerEditorTab`)

### 新セクション「Cast→Object 出現予測」

`DrawObjectAoeRuleEditor` の下に追加：

```
[+] Cast→Object 出現予測（オブジェクト予告）

┌─────────────────────────────────────────────────────┐
│ 有効│Cast名      │Object名    │遅延 │位置 │R  │信頼度 │
├─────────────────────────────────────────────────────┤
│ ✓ │パラデイグマ │ケツアクアトル│14.9s│4箇所│6m │95%  │ [編集] [削除]
└─────────────────────────────────────────────────────┘

[ルール追加]  [録画から再学習]  [全削除]
```

各エントリの編集ボタン → モーダル / ポップアップで詳細編集：

- Cast ID / Cast Name / Source 名
- 遅延秒数（手動上書き可）
- Object Name / Data ID
- 出現位置（マップ視覚化、ドラッグで位置編集）
- AoE 形状・半径・内径・扇角・半幅
- 持続秒数・色
- 信頼度（読み取り専用、自動更新）
- ソース（recording / manual / merged）

### 全自動学習結果の検証 UI

「録画から再学習」ボタンで `PredictedObjectSpawnLearner` を起動し、既存の predicted_object_spawns を更新（手動編集分は保持）。

## 6. エッジケース対応

### 6.1 同名 actor が複数 data_id を持つ（変身演出）

- 学習時：`(ObjectName, ObjectDataId)` ペアで識別
- 発火時：data_id 指定があれば object 実出現の検知時に data_id 一致をチェックして確定格上げ
- 既存修正（`obj_aoe_3834_C79A4CDC` の data_id=14388 必須化）と整合

### 6.2 出現位置が毎回ランダム

- 学習時に位置クラスタリングで `PositionVariance` を計算
- `PositionVariance > しきい値` なら `IsPositionStable=false`
- 発火時、`IsPositionStable=false` の spawn は予告描画せず、実位置出現を待って `AddObjectAoeService` 経由で描画

### 6.3 複数体出現の組数が変動

- 学習時に `ObservedSpawnCount` の最頻値を採用
- 4 体 / 8 体 が混在する場合は信頼度を下げる

### 6.4 同 cast から複数種の object 出現

- 1 つの cast に対して複数 `PredictedObjectSpawn` を作る（ObjectName 別）
- 例：「召喚 cast → ボス本体 (data_id=A) と add 4 体 (data_id=B)」は 2 つの spawn として管理

### 6.5 ユーザーが手動編集したエントリ

- `Source = "manual"` を立てて、自動学習で上書きしない
- マージ時：手動エントリは temp に退避 → 自動学習 → 手動エントリを戻す

### 6.6 誤学習の対処

- ユーザーが UI で削除（`Enabled=false` か行削除）
- 「全削除 → 再学習」でリセット
- 信頼度しきい値（観測 2 回以上、ファイル数 2 以上）でデフォルト無効化

### 6.7 戦闘中のリアルタイム学習

- 戦闘中に新しい cast→object パターンを観測した場合は **戦闘終了後にバックグラウンド学習**
- 学習結果は次戦闘から有効

## 7. 段階的実装計画

### Phase 1: データ構造とローダー（基盤）
- `PredictedObjectSpawn` / `SpawnPoint` モデル定義
- `StrategyProfile.PredictedObjectSpawns` 追加
- JSON シリアライズ対応
- ユニットテスト：JSON ラウンドトリップ

### Phase 2: 学習パイプライン
- `RecordingScanner.ExtractCastObjectPairs` 拡張
- `PredictedObjectSpawnLearner` 新規実装
- 信頼度フィルタ、位置クラスタリング
- ユニットテスト：人工録画データで学習結果検証

### Phase 3: 発火パイプライン（描画）
- `PredictedObjectSpawnService` 新規実装
- cast_start / cast_complete 購読、zone 発火
- `ObjectAppearedEvent` で予告→確定格上げ
- 二重描画防止 dedup
- 統合テスト：人工イベント流して描画パス確認

### Phase 4: UI 拡張
- `TriggerEditorTab` に新セクション追加
- リスト表示、編集、削除、追加、再学習ボタン
- 位置プレビュー（既存のアリーナマップ UI を流用）

### Phase 5: 月の底で検証 → 他コンテンツへ
- ユーザーが月の底を録画 → 学習 → 次回戦闘で予告動作確認
- 雲廊、暗闇の領域、武神の闘技場 など他ゾーンで録画があるか確認
- それぞれで自動学習されるか観測
- フィードバックを受けて Phase 1〜4 の調整

### Phase 6: ドキュメント
- ユーザー向け：「自動学習の仕組み」「設定の見方」を README に追記
- 開発者向け：`docs/predicted-object-spawn-design.md` を最終形に更新

## 8. 影響範囲

### 新規ファイル

- `src/FfxivEchoes/Triggers/Models/PredictedObjectSpawn.cs`
- `src/FfxivEchoes/Triggers/Models/SpawnPoint.cs`
- `src/FfxivEchoes/Triggers/PredictedObjectSpawnLearner.cs`
- `src/FfxivEchoes/Triggers/PredictedObjectSpawnService.cs`
- `src/FfxivEchoes.Tests/PredictedObjectSpawnLearnerTests.cs`

### 拡張ファイル

- `Triggers/Models/TriggerFile.cs` （`StrategyProfile.PredictedObjectSpawns` 追加）
- `Recording/RecordingScanner.cs` （`ExtractCastObjectPairs` メソッド追加）
- `Windows/Tabs/TriggerEditorTab.cs` （UI セクション追加）
- `Plugin.cs` （`PredictedObjectSpawnService` の DI 登録）

### 影響を受ける既存機能

- `AddObjectAoeService.OnAppear / FireGroup`：予告 zone との dedup ロジック追加
- `MinimapWindow.AddArenaView`：予告 zone の色や視覚的区別を追加
- `ActorTrackedAoeService.OnTriggerFired`：予告 zone を Predicted フェーズで登録

## 9. リスクと対策

| リスク | 対策 |
|---|---|
| 学習データ不十分 | 信頼度しきい値（観測 2 回以上、ファイル数 2 以上）。低信頼度はデフォルト無効 |
| 位置が変動するメカニクス | `IsPositionStable=false` の場合は予告描画せず実出現待ち |
| 既存実装との二重描画 | dedup ロジック追加 (suppress 機構の流用) |
| コード規模 | Phase 分割。各 Phase 単独で動作確認可能な粒度 |
| ユーザーの誤学習体験 | UI で削除・再学習可能、信頼度表示で判断可能 |
| 既存 `object_aoe_rules` との関係混乱 | `predicted_object_spawns` は「事前予告」、`object_aoe_rules` は「実出現後の描画」と役割分離。ドキュメント明記 |

## 10. 既存機能との関係

| 既存機能 | 役割 | この機能との関係 |
|---|---|---|
| `object_aoe_rules` | object 実出現後の AoE 描画ルール | そのまま残す。`PredictedObjectSpawn` の AoE 形状ソースとして参照 |
| `PredictedCastReminderService` | cast の事前 TTS / overlay 警告 | そのまま残す（描画なし、警告のみ） |
| `AddObjectAoeService.OnAppear / FireGroup` | object 実出現で `object_aoe_rules` を発火 | そのまま残す。実位置描画パスとして継続 |
| `MechanicTriggerService` | ユーザー定義 mechanic の cast_start 発火 | そのまま残す。手動 mechanic は引き続き優先 |
| **新規** `PredictedObjectSpawnService` | **cast 検知時点で事前 AoE 予告描画** | 上記すべてと並列に動作 |

## 11. 完了条件

このフェーズの「完了」とは：
- 月の底でパラデイグマ → ケツアクアトル予告が **時点 17 秒〜32 秒で四隅に表示される**
- 他のゾーン（録画があるもの）で「召喚系 cast → add 出現」パターンを自動学習して同様に予告できる
- UI で誤学習を削除・修正できる
- ユニットテストが通る、回帰が無い

## 12. 着手前の確認事項

実装に入る前にユーザーの確認が必要：

1. 設計の方向性は OK か
2. Phase 順序の優先度はこれで良いか
3. 既存機能（特に `object_aoe_rules`）の扱いは設計通りで良いか
4. UI のレイアウト方針（既存タブに追加 vs 別タブ新設）はどっち希望か
5. 「予告色」（薄いオレンジ提案）の見え方の好みはあるか
