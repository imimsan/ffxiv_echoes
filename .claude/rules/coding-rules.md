# コーディング規約（C# / Dalamud）

ffxiv-echoes は **C# / .NET** で書かれた Dalamud プラグイン。本ファイルでコード品質・スタイル・設計の最低ラインを定める。

---

## 基本原則

1. **Dalamud のコーディング慣習に従う**：[Dalamud Plugin Development Wiki](https://github.com/goatcorp/Dalamud) のガイドラインを優先。
2. **Microsoft 公式 C# コーディング規約**を二次的に参照：[C# coding conventions](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions)
3. **規約より読みやすさ**：規約に従うことが目的化したら立ち止まる。
4. **小さく作って動かす**：MVP→フェーズ2→フェーズ3 の流れに従い、先取り抽象化を避ける。

---

## C# 言語仕様

- **ターゲット**：.NET 8+（Dalamud の現行 SDK が要求するバージョンに追従）
- **言語バージョン**：`<LangVersion>latest</LangVersion>` を csproj に明示
- **nullable 参照型**：プロジェクト全体で有効化（`<Nullable>enable</Nullable>`）。`?` を意識的に使う。
- **暗黙 using**：`<ImplicitUsings>enable</ImplicitUsings>` を有効。
- **トップレベルステートメント**：プラグインのエントリポイント以外では使わない。

---

## ファイル・ディレクトリ構成（提案）

```
src/
├── FfxivEchoes/                          # メインプロジェクト（プラグイン本体）
│   ├── Plugin.cs                         # IDalamudPlugin 実装（エントリポイント）
│   ├── Configuration.cs                  # IPluginConfiguration 実装
│   ├── Capture/                          # M3：イベントキャプチャ層
│   │   ├── IEventCapture.cs
│   │   ├── CastEventCapture.cs
│   │   ├── StatusEventCapture.cs
│   │   └── ...
│   ├── Recording/                        # M4：ロガー
│   │   ├── BattleRecorder.cs
│   │   └── JsonLinesWriter.cs
│   ├── Triggers/                         # M5/M6：トリガー
│   │   ├── TriggerEngine.cs
│   │   ├── TriggerLoader.cs
│   │   ├── Models/                       # JSON シリアライズ対象 POCO
│   │   │   ├── TriggerDefinition.cs
│   │   │   ├── MatchCondition.cs
│   │   │   └── ActionDefinition.cs
│   │   └── Matching/                     # マッチングロジック
│   ├── Actions/                          # M7：アクションディスパッチャ
│   │   ├── IActionHandler.cs
│   │   ├── TtsHandler.cs
│   │   ├── WavHandler.cs
│   │   └── OverlayTextHandler.cs
│   ├── SafeZone/                         # F4-F7：安置計算プリセット
│   │   ├── ISafeZonePreset.cs
│   │   └── Presets/
│   ├── Ui/                               # M8/F12：ImGui UI
│   │   ├── MainWindow.cs
│   │   ├── Pages/
│   │   └── LiveHud/
│   └── Utils/
└── FfxivEchoes.Tests/                    # ユニットテスト
    └── ...
```

実際の最終構成は M2 の段階で確定する。上記は提案。

---

## 命名規則

| 対象 | 規則 | 例 |
|------|------|-----|
| クラス・構造体・enum・interface | PascalCase | `TriggerEngine`, `ICastEvent` |
| interface | `I` プレフィックス | `IEventCapture` |
| メソッド・プロパティ | PascalCase | `LoadTriggers()` |
| ローカル変数・引数 | camelCase | `castId`, `targetActor` |
| private フィールド | `_camelCase` | `_logger`, `_configuration` |
| public フィールド | PascalCase（基本は使わずプロパティ化） | — |
| const | PascalCase | `MaxBufferSize` |
| static readonly | PascalCase | `DefaultVoice` |
| 型パラメータ | `T` プレフィックス | `TEvent`, `TPayload` |
| ファイル名 | クラス名と一致 | `TriggerEngine.cs` |
| プロジェクト名 | `FfxivEchoes.<Module>` | `FfxivEchoes.Tests` |

- **ハンガリアン記法禁止**：`strName`, `iCount` などは使わない。
- **略語**：3 文字以下は全大文字（`Id`, `Tts`, `Wav` ではなく `ID`, `TTS`, `WAV`）。ただし C# 慣習に従い、識別子では PascalCase の `Id`、`Tts` を採用する（一貫させる）。**本プロジェクトは PascalCase 形（`Id`, `Tts`, `Wav`, `Hp`）に統一**する。

---

## フォーマット

- **インデント**：スペース 4 つ。タブ禁止。
- **改行**：LF（`.gitattributes` で `* text=auto eol=lf` を強制）。
- **行末**：行末空白なし。
- **1 行の長さ**：120 文字目安。長くなる場合は意図的に改行。
- **ブレース**：Allman 形式（K&R ではなく改行して開く）。
  ```csharp
  if (condition)
  {
      DoThing();
  }
  ```
- **using 文**：`System.*` を最上段、その後にアルファベット順。`global using` は `Plugin.cs` か専用ファイルにまとめる。
- **`var` 使用**：右辺で型が自明な場合のみ（`new`、リテラル、明示的キャスト）。それ以外は明示的型を書く。
- **式形式メンバー**：1 行で済むプロパティ・メソッドは `=>` で書いてよい。
- **`.editorconfig`** をリポジトリルートに置き、フォーマットを CI で強制する。

---

## 設計指針

### 依存注入

- Dalamud のサービスは `[PluginService]` 属性で注入。
- それ以外の依存（自作サービス）はコンストラクタ注入。サービスロケータパターン禁止。

### 例外処理

- **想定済み失敗**は戻り値（`Result<T>` 的なパターン or `bool TryXxx`）で表現。
- **想定外失敗**のみ例外で扱う。
- **catch する場合は最低限ログを出す**：`PluginLog.Error(ex, "...")`。`catch (Exception) { }` のような握りつぶし禁止。
- **`Exception` 全捕捉は最上位の境界のみ**：イベントハンドラ・ループのトップなど、プラグインがクラッシュすると Dalamud ごと巻き込む箇所。

### 非同期

- Dalamud のフレーム処理は同期。重い処理（ジオメトリ計算・ファイル I/O）は `Task.Run` または専用スレッドで実行し、結果を `Framework.RunOnFrameworkThread` で UI スレッドに戻す。
- `async void` は禁止（イベントハンドラを除く）。
- `ConfigureAwait(false)` は Dalamud 非 UI コードで明示。

### null 安全

- nullable 参照型を有効にし、`?` で許容を明示。
- public API では `null!` を返さない。`Result` 型 or 明示的にドキュメント化。
- DI されるサービスは `[PluginService]` で nullable でない前提が成立。

### スレッド

- ImGui 描画は必ずメインスレッド。
- `ObjectTable` 等の Dalamud API は基本メインスレッド。
- 録画バッファは並行アクセスがあるため `lock` または `Concurrent*` を使う。

---

## ログ・デバッグ

- ログ出力は **Dalamud の `IPluginLog`** を使う。`Console.WriteLine` 禁止。
- ログレベル：
  - `Verbose`：詳細トレース（デバッグ用）
  - `Debug`：開発時の動作確認
  - `Information`：通常のライフサイクル（プラグイン読込・設定保存など）
  - `Warning`：想定外だが継続可能な状況
  - `Error`：失敗。例外発生時は `Error(ex, "...")` で例外を含めること。
  - `Fatal`：プラグインを継続不可能にする状況
- リリースビルドで `Verbose` / `Debug` を出さない。

---

## コメント

- **基本書かない**。命名で説明する。
- 書くべきもの：
  - 非自明な制約（「Dalamud の制約により...」）
  - パフォーマンス上の選択（「ここはメインスレッドのため...」）
  - バグ回避（「FFXIV のステータス ID は uint で受けるとオーバーフローする」）
  - 仕様書参照（「`SPEC.md §4.3` に従う」）
- **書かないもの**：
  - コードを言い換えただけのコメント
  - PR・issue・著者・日付（git で見れる）
  - 削除済みコードの跡（消す）

---

## テスト

- フレームワーク：**xUnit**（NUnit より軽量・スコープ制御が明確）
- アサーション：xUnit 標準 or `FluentAssertions`
- 命名：`MethodName_StateUnderTest_ExpectedBehavior` または日本語の `_Should_` 形式
  - 例：`TriggerEngine_未ロード時_例外を投げる`
  - 例：`MatchCondition_StatusIdが一致_trueを返す`
- **Dalamud 依存はモック**：`IDalamudPlugin` 関連はインターフェース化してモック差し替え可能にする。
- ロジック層（`Triggers`, `SafeZone`, `Recording` の純粋関数部分）は **必ずテストを書く**。
- UI（ImGui）はテストしない。動作確認は手動で行う。

---

## NuGet パッケージ

- 追加時はユーザーに用途を共有し、確認を取ってから追加。
- ライセンス確認（GPL 系は注意）。
- バージョンは固定（`Version="1.2.3"`）。範囲指定（`[1.0.0,2.0.0)`）は必要時のみ。
- `packages.lock.json` をコミット。

---

## パフォーマンス

`SPEC.md §11` の目標：
- 1 フレームあたり 1ms 未満
- 常駐メモリ 100MB 以下

実装上の注意：
- フレーム呼び出しの中で `LINQ` や `string` 連結を多用しない（GC 負荷）。
- `ObjectTable` の走査は必要時のみ・差分検知で。
- `Span<T>` / `Memory<T>` を活用して allocation を抑える（必要箇所のみ）。
- ImGui 描画は仮想スクロールで表示中要素のみ。

早期最適化を避ける。まずプロファイラで計測してから手を入れる。

---

## ローカライゼーション

- **日本語クライアント前提**（`SPEC.md §1.5`）。
- UI 文字列は当面ハードコード可。ただし `Resources.resx` などの差し替え機構を用意しておく（フェーズ外で多言語化検討）。

---

## エディタ・フォーマッタ

- **Visual Studio 2022** または **JetBrains Rider** を推奨。
- `dotnet format` を CI で強制：`dotnet format --verify-no-changes`
- `.editorconfig` で規約をエディタに伝える（M1 で導入）。

---

## 参照

- [Dalamud Plugin Development](https://github.com/goatcorp/Dalamud)
- [Microsoft C# Coding Conventions](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions)
- [.NET API Design Guidelines](https://learn.microsoft.com/en-us/dotnet/standard/design-guidelines/)
- `.claude/rules/git-rules.md` — git 運用
- `.claude/rules/security-rules.md` — 機密情報の扱い
