# 俯瞰図 AoE 事前描画（予測レイヤ）＋録画位置記録 設計書

日付: 2026-06-11
対象: 絶妖星乱舞（被検世界「シグマ」V4.0）での実用を主目的とした、タイムライン予測ベースの AoE 範囲事前表示。

## 1. 目的

タイムライン HUD（UpcomingEventsWindow）が予測している「次に来るキャスト」について、その AoE 範囲（円・ドーナツ・扇・直線・two_side_cleave 等の実効範囲）を、詠唱開始の **N 秒前から俯瞰図（MinimapWindow）に薄く事前描画**する。真偽ギミックのように「見た目と実効範囲が異なる」技も、safe_call_dictionary の cast_id 別ギミック定義を通じて実効範囲で表示する。

あわせて、将来の「録画位置ベースの事前描画」（案 B 本体）に向け、録画 jsonl の cast_start に**キャスト者の位置・向きを記録**し始める（読み手は本スコープ外）。

## 2. 背景・制約

- 予測ミニマップ描画は過去に実装済みだが無効化された（`PredictedCastReminderService.cs:260-266`）。原因は source actor 解決の**最大 HP フォールバック**が名前衝突 actor を誤解決し「マップ中央に謎のドーナツ」を描いた回帰。本設計は再有効化ではなく、原因を潰した新経路。
- 予測 TTS（`PredictAdvanceWarningSec`、絶ケフカでは JSON で無効化済み）とは**完全に独立**。TTS・overlay は一切発行しない（無音化/二重読み問題を再発させない）。
- パフォーマンス制約: フレーム内 1ms 未満（SPEC §11）。戦闘中のファイル I/O 禁止（既存の戦闘中キャッシュ固定方針に従う）。
- 絶ケフカはランダム分岐・連続プルでフェーズが進む。タイムライン側のセグメント再アンカ・分岐棄却・フェーズ絞り込み（fix/phase-transition-reset の作業を含む）が時刻の正である。

## 3. 全体アーキテクチャ

```
UpcomingEventsWindow.Draw()  （毎フレーム、補正済み upcoming リスト計算 — 既存）
        │  items（Time/Label/Source/EventType + 新規: CastId）
        ▼
UpcomingAoePreviewService（新規・メインスレッド）
        │  ・残り ≤ PredictedAoeAdvanceSec の cast_start 予測を抽出
        │  ・cast_id → 形状解決（キャッシュ済み）
        │  ・source actor をライブ解決（EntityId キャッシュ + SearchById）
        │  ・cast_start 観測で該当予測を即削除（確定表示へ昇格）
        ▼
MinimapWindow 予測レイヤ（新規 API: SetPredictedAoe / UpdatePredictedAoe）
        ・低アルファ + 点線 + 「技名 残り Xs」ラベルで描画
        ・確定描画（既存 ArenaItem）とは独立したリスト
```

- **データソースの一意性**: タイムライン HUD が表示しているものだけが、同じ残り秒で俯瞰図にも出る。セグメント再アンカ・同期オフセット・分岐棄却・フェーズ絞り込み・重複排除はすべて上流（既存ロジック）で適用済み。
- **動作条件**: タイムライン HUD が表示中（戦闘中かつ `show_timeline` 有効）のときのみ動作する（承認済みの制約）。

## 4. コンポーネント設計

### 4.1 UpcomingItem / UpcomingTemplate への CastId 追加

- `UpcomingTemplate` と `UpcomingItem` に `Id`（cast_id 文字列、"0x…" 形式、ノート等は null）を追加。
- `RecordingTimelinePrediction.Id` は既に存在するため、テンプレート構築時に引き渡すだけ。

### 4.2 UpcomingAoePreviewService（新規）

責務: upcoming リストから予測 AoE 表示対象を選び、ミニマップ予測レイヤを維持する。

- 入力: `UpcomingEventsWindow.Draw()` から毎フレーム `Publish(IReadOnlyList<UpcomingItem> items, double nowRel)` で受け取る（窓非表示時は呼ばれない → レイヤは stale タイムアウトで自動クリア）。
- 選択規則（純粋関数 `SelectPreviewCandidates`）:
  - `EventType == "cast_start"` かつ `Id` あり
  - `0 < Time - nowRel <= PredictedAoeAdvanceSec`
  - 形状解決に成功し、スキップ規則に該当しない
- スキップ規則（旧 `TryDrawPredictedMarker` の知見を流用、純粋関数化）:
  - raid_wide マーク済み（`AutoSafeCallPlanner.IsRaidWide`）
  - 超大型 AoE: 円形（CastType 2/5）半径 ≥ 25m、その他 ≥ 30m（全体攻撃扱い）
  - `AutoSafeCallPlanner.ShouldSuppressMinimap` が真
  - source actor 未解決（**名前完全一致のみ。最大 HP フォールバックは使わない**）
- 形状解決（`ResolvePreviewGeometry`、cast_id 単位でキャッシュ）:
  1. safe_call_dictionary override（`AutoSafeCallPlanner.CreateKnown` → `KnownAoeGeometry.TryCreate`）— 真偽・two_side_cleave 等の実効範囲
  2. Lumina（`AoeResolver.Resolve` → CastType + EffectRange）
  3. どちらも解決不可 → 表示しない
  - キャッシュは zone 変更・トリガー Reload で破棄（Reloaded はワーカースレッド発火のため dirty フラグのみ立て event 処理はフレームスレッドで行う）
- actor 解決: 「source 名 → EntityId」キャッシュ + `IObjectTable.SearchById`。miss 時のみ ObjectTable 再走査（0.5 秒に 1 回まで）。
- 昇格: `CastStartedEvent` を購読し、同一 cast_id（無ければ同一 source+label）の予測アイテムを即削除。以降は既存の確定経路（AutoTelegraphService）が描く。
- 排他候補（真偽など）: 同時刻帯（±3 秒）の別 cast_id 変種が両方 upcoming に残っている場合、**両方を候補として薄表示**する。分岐確定後は上流の分岐棄却で自動的に 1 つへ絞られる。
- TTL 安全弁: 候補選択が毎フレーム「残り時間 > 0」を要求するため、予測が外れたアイテムは時刻経過で自動的に描画対象から外れる。`Publish` が 1.5 秒以上呼ばれない場合はミニマップ側でレイヤをクリア（窓が閉じた場合の残留防止）。

### 4.3 MinimapWindow 予測レイヤ

- 新規構造体 `PredictedAoeItem`（cast_id、ラベル、形状 spec、source EntityId、残り秒、向き追従フラグ）。
- `UpcomingAoePreviewService` が小さなリストを所有し、`MinimapWindow` は描画時に参照（lock 保護のスナップショット差し替え。毎フレーム alloc しない: 内容が変わったときのみ配列再構築）。
- 描画スタイル: alpha ≈ 0x40 の塗り + 点線輪郭 + 「ラベル 7s」テキスト。確定 ArenaItem の描画ヘルパ（DrawActualAoeShape 系）を流用し、スタイル引数で予測表現にする。
- 位置・向き: 描画時（メインスレッド）に SearchById で actor の現在位置・Rotation を読む。向き依存形状（扇・直線・half_plane）はボスの現在向きで毎フレーム追従し、点線で「未確定」を明示。
- ミニマップが何らかの理由で非表示の場合: `IsOpen = true` にする条件は既存 AddArenaView と同じ挙動に合わせる（予測のみで強制オープンはしない。確定時に開く既存挙動を変えない）。

### 4.4 設定（Configuration + 設定 UI）

- `ShowPredictedAoeOnMinimap` (bool, default **true**)
- `PredictedAoeAdvanceSec` (double, default **10.0**、1–30 にクランプ)
- 設定タブにチェックボックスとスライダーを追加。

### 4.5 録画位置記録（案 B 仕込み）

- `CastStartedEvent` に `SourceWorld (Vector3?)` と `SourceRotation (float?)` を追加（キャプチャ地点で actor から取得。取得不能なら null）。
- `EventSerializer` の cast_start 出力に `source_x/y/z`（小数 3 桁丸め）と `source_rot`（ラジアン、4 桁丸め）を追加。null なら省略（既存の optional フィールドと同じ流儀）。
- 既存の読み手（RecordingAggregationReader 等）は未知フィールドを無視するため互換性影響なし。読み手・集計・再生は本スコープ外。

## 5. データフロー（1 フレーム）

1. `UpcomingEventsWindow.Draw()` が `CollectUpcoming(nowRel)` を計算（既存・キャッシュヒット時は軽量）。
2. **CollectUpcoming の直後**（表示用の同名 ±3 秒 dedup より前）に `_aoePreview?.Publish(items, nowRel)` を呼ぶ（新規 1 行）。dedup 前のリストを渡すことで、真偽など同名別 cast_id の両候補が予測レイヤに届く（HUD 行表示の dedup は従来通り）。
3. `Publish` は候補選択（≤16 件の走査）→ 形状キャッシュ参照 → 予測レイヤのリスト差分更新。構成が変わらないフレームでは alloc ゼロ。
4. `MinimapWindow.DrawCore` が予測レイヤを描画（actor 位置の SearchById ≤ 数回）。

## 6. エラー処理

- 形状解決・actor 解決の失敗は「表示しない」に倒す（誤誘導ゼロ原則）。例外は `FrameErrorThrottle.Report` で集約ログ（既存パターン）。
- `Publish` 内は全体を try/catch で囲い、失敗してもタイムライン描画本体に影響させない。

## 7. パフォーマンス予算

| 処理 | 頻度 | コスト |
|------|------|--------|
| 候補選択（≤16 件走査） | 毎フレーム | O(16)、alloc なし（構成不変時） |
| 形状解決 | cast_id 初見時のみ | 辞書参照 + Lumina 1 行読み（キャッシュ） |
| actor 解決 | 毎フレーム ≤ 数体 | SearchById O(1)、miss 時のみ再走査（≥0.5s 間隔） |
| 予測レイヤ描画 | 毎フレーム ≤ 数件 | ImGui プリミティブ数件 |
| ファイル I/O | なし | — |

## 8. テスト（FfxivEchoes.Tests/Program.cs に追加）

1. `SelectPreviewCandidates`: 窓内/窓外/cast_start 以外/Id なしの選別
2. スキップ規則: raid_wide・超大型 AoE・suppress の各ケース
3. 排他候補: 同時刻帯の別 cast_id 変種が両方候補になる / 片方棄却後は 1 つ
4. 昇格: cast_start 観測で同一 cast_id の予測が消える
5. TTL 安全弁: 期限超過アイテムの除外、Publish 停止時のクリア
6. EventSerializer: source_x/y/z・source_rot の出力（null 時は省略）

Dalamud 型（IObjectTable 等）はテストから触れないため、選択・スキップ・昇格規則は Dalamud 非依存の純粋関数として `UpcomingTimelinePolicy` か新規 `PredictedAoePreviewPolicy` に置く。

## 9. スコープ外

- 床塗り（ワールドオーバーレイ）への事前描画（`ActorTrackedAoeService.TrackPredicted` は存在するが今回は使わない）
- 録画位置データの読み手・「録画位置での事前描画」（案 B 本体）
- 予測 TTS の再有効化
- タイムライン行への範囲テキスト注記

## 10. リスクと対応

| リスク | 対応 |
|--------|------|
| 向き依存形状の事前向きズレ | 点線+低アルファで「未確定」を明示、毎フレーム追従、詠唱開始で確定表示に昇格 |
| 真偽の変種誤表示 | 分岐確定前は両候補表示（断定しない）、確定後は上流棄却で 1 本化 |
| 予測時刻ズレによる残留 | Time+2s の TTL 安全弁 |
| タイムライン窓非表示時の残留 | Publish 停止 1s でレイヤ自動クリア |
| フレーム負荷 | I/O ゼロ・キャッシュ済み解決・差分更新（§7） |
