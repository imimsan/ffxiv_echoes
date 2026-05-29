# 録画バージョンゲート修正 設計書

- 日付: 2026-05-29
- ブランチ: `fix/recording-version-gate`（`develop` 派生）
- 関連: ケフカ（被検世界「シグマ」V4.0）戦でプラグインが「使い物にならない」事象の調査から派生

## 背景・根本原因（確定）

ケフカ戦の録画17本・`dalamud.log`（287,635行中 96,801行=34% が Echoes 由来）・クラッシュログを解析した結果：

- `FfxivEchoes.csproj` の `<Version>` は `0.0.1.0`。録画 meta には `Manifest.AssemblyVersion`（=`0.0.1.0`）が書かれる（`BattleRecorder.cs:106`）。
- 一方 `RecordingAggregationReader.cs:22` の `MinSupportedPluginVersion = "0.1.0"` が下限。
- 結果 `0.0.1.0 < 0.1.0` で、**実機が書いた録画は1本残らず「古い録画フォーマット」警告**になる。
- `Aggregate()` は戦闘中の予測系サービスや設定ウィンドウ（`TriggerEditorTab` の ImGui 毎フレーム経路）から高頻度に呼ばれ、その都度・全ファイルで警告を吐く。録画が増える（17本）ほど悪化し、`dalamud.log` が 100MB 上限まで肥大 → フレーム遅延で全機能不調 → 最終的にクラッシュ（`C0000005` はゲーム側UI解放中で負荷由来の二次被害と判断、本修正の対象外）。
- テストデータ（`Program.cs`）は全て `plugin_version="0.1.0"` ハードコードのため、この不整合は**テストでは再現しない実機限定バグ**だった。

### 設計上の重要な事実

- `IsPlayer` は「録画に書くフラグ」ではなく、**`BattleRecorder.OnAnyEvent` が録画を skip するための内部判定**（`BattleRecorder.cs:155-186`）。`status_gain/lose/update`・`action_used`・`hp_change`・`object_appear` は IsPlayer=true なら録画されない（除外方式）。
- したがって新録画には PC イベントがそもそも入らない。**集計側（`IsPartyRelated`/`PcSkillNameFilter`）は変更不要**で、旧録画の名前ベース fallback として温存する。
- `MinSupportedPluginVersion="0.1.0"` の意図は「IsPlayer 除外ロジックが効くようになった版」。だが版を上げ忘れたまま運用したため、除外済み録画が「古い録画」と誤判定されていた。
- `CastStartedEvent`/`CastCompletedEvent`/`CastCanceledEvent` だけは `IsPlayer` フィールドが無く、除外漏れしている。ただし `CastCapture` は `IBattleNpc` のみ対象（`CastCapture.cs:57`）なので、漏れているのは **PC召喚物（`IsPetBnpc`）と PCアクションID（`IsPlayerAction`）の詠唱**のみ（PC本人の詠唱は元々捕捉されない）。

## スコープ（2段階・同一ブランチ内でコミット分割）

### Phase A（コミット1）— 警告氾濫の停止【応急】

`Aggregate()` が何度呼ばれても「古い録画」警告でログが膨張しないようにする。

- 変更: `RecordingScanner.Aggregate()`（`RecordingScanner.cs:77-83`）。`onWarning` を、同一パスの「古い録画」警告を**インスタンス内で1回だけ**転送するラッパで包む。
  - `RecordingScanner` は `Plugin.cs:182` で単一生成され全サービスで共有 → セッションを通じて実質1回。
  - 抑制キーはファイルパス（`HashSet<string>` を `RecordingScanner` のフィールドに保持、`lock` で保護）。
  - `InvalidDataException`（古い録画）由来の警告のみ抑制対象とし、`IOException`/`JsonException` 等の実害ある警告は毎回通す。
- `RecordingAggregationReader`（static 純粋ロジック）は状態を持たせず現状維持。
- 効果: 旧 `0.0.1.0` 録画にも恒久的に有効。

### Phase B（コミット2〜）— 版 0.1.0 と Cast 除外漏れ修正【本来設計】

- **B1**: `FfxivEchoes.csproj` の `<Version>0.0.1.0</Version>` → `<Version>0.1.0</Version>`。マニフェスト `FfxivEchoes.json` は `AssemblyVersion` を持たず csproj 由来なので変更不要。これで新録画が下限を満たし警告対象外に。
- **B2**: Cast 系の PC 除外漏れを塞ぐ。
  - `GameEvents.cs`: `CastStartedEvent`/`CastCompletedEvent`/`CastCanceledEvent` に `bool IsPlayer = false` を追加（末尾・既定値ありで後方互換）。
  - `CastCapture.cs`: `LuminaPcDetector` を DI。判定は `IsPetBnpc(actor) || IsPlayerAction(actionId)`（actor は `IBattleNpc` 限定なので PC本人判定は不要）。`CastState` record に `bool IsPlayer` を保持し、Started/Completed/Canceled の全発行箇所（初観測 Start、遷移 Start、Completed、Canceled、切替時 Cancel+Start、ObjectTable 消失時 Cancel）で設定。Completed/Canceled は `prev.IsPlayer` を使う。
  - `Plugin.cs`: `CastCapture` 生成箇所に `pcDetector` を注入。
  - `BattleRecorder.cs`: `OnAnyEvent` に `CastStartedEvent`/`CastCompletedEvent`/`CastCanceledEvent` の `IsPlayer` skip を追加。
- 集計側・`PcSkillNameFilter`・`EventSerializer` は変更なし（cast は除外方式なので録画に載らず、`is_player` 出力も不要）。

## テスト計画（各 Phase で失敗テスト先行＝TDD）

`FfxivEchoes.Tests`（`OutputType=Exe` の独自ランナー、`Program.cs` の `tests` リストに `(name, method)` を追加）。

- Phase A: `RecordingScanner` 相当の onWarning ラッパが、同一パスの `InvalidDataException` 警告を複数回 Aggregate しても1回だけ転送することを検証。`IOException` は毎回通すことも検証。
  - 純粋ロジックとして抽出できるなら静的メソッド化してテスト（DI 依存を避ける）。
- B2: `CastCapture` の IsPlayer 判定を純粋関数（`ShouldMarkCastAsPlayer(isPetBnpc, isPlayerAction)` 等）に抽出し、`ActionEffectCapture.ShouldMarkActionAsPlayer` と同様にテスト（PC召喚物/PCアクションは true、ボス詠唱は false）。
- B1: `0.1.0` 録画は「古い録画」警告ゼロ、`0.0.1.0` は警告対象（だが Phase A で1回）を `AggregateFiles` レベルで検証。

## リスク・後方互換

- 昨日の17本（`0.0.1.0`）は版を上げても「古い録画」のまま → Phase A の抑制で警告1回・実害なし（名前 fallback が機能）。
- `IsPlayerAction` はボス技に `false`、`IsPetBnpc` は OwnerId=PC のみ true なので、Cast 除外でボス詠唱を誤除外しない。
- `<Version>` を参照する他箇所（`Manifest.AssemblyVersion` を使う `GeneralSettingsTab` のバージョン表示など）は表示が `0.1.0` に変わるだけで害なし。
- 「毎フレーム全ファイル読込」の CPU 負荷自体は本修正のスコープ外（ログ氾濫解消で実用復帰の見込み。残れば別途 Aggregate キャッシュを提案）。

## 実装順序

1. Phase A: テスト追加（失敗）→ `RecordingScanner` 実装 → テストパス → コミット。
2. Phase B2: テスト追加（失敗）→ `GameEvents`/`CastCapture`/`Plugin`/`BattleRecorder` 実装 → テストパス。
3. Phase B1: csproj 版上げ → 全ビルド・全テスト。
4. 多角的レビュー（correctness/regression/backward-compat/thread-safety/test-coverage/coding-rules）→ 反映 → コミット。
