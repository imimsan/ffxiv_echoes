# 開発環境セットアップ（M1）

このドキュメントは [roadmap.md](../roadmap.md) の **M1（開発環境構築）** に対応する。Dalamud プラグインを手元の Windows でビルドし、ホットリロードで動作確認できる状態にするための手順。

## 前提

- Windows 10/11
- FFXIV 日本語クライアント
- [XIVLauncher](https://goatcorp.github.io/) をインストール済み
- 一度 XIVLauncher 経由でゲームを起動して **Dalamud がインストール済み**であること（`%AppData%\XIVLauncher\addon\Hooks\dev\` が存在することを確認）

## 必要なツール

| ツール | バージョン | 備考 |
|--------|-----------|------|
| .NET SDK | **10.0** 以上 | 現行の `Dalamud.NET.Sdk/15.0.0` が要求。SamplePlugin の CI も dotnet 10 |
| Git | 任意 | 開発に必須 |
| Visual Studio 2022 / Rider | 任意 | エディタは好み（VS Code でも可） |
| PowerShell 5.1+ または bash | 任意 | コマンド実行用 |

### .NET SDK が未インストールの場合

```powershell
# winget で .NET 10 SDK を入れる例（管理者権限不要）
winget install Microsoft.DotNet.SDK.10
```

または公式サイト：[https://dotnet.microsoft.com/download/dotnet/10.0](https://dotnet.microsoft.com/download/dotnet/10.0)

確認：

```bash
dotnet --info
# .NET SDKs installed: 10.0.x が出ていればOK
```

## ビルド

リポジトリルートで：

```bash
dotnet restore
dotnet build --configuration Debug
```

ビルド成果物は次の場所に出力される：

```
src/FfxivEchoes/bin/x64/Debug/FfxivEchoes/
├── FfxivEchoes.dll
├── FfxivEchoes.json   ← プラグインマニフェスト
└── ...依存DLL
```

`Dalamud.NET.Sdk` が自動的に Dalamud アセンブリ（`%AppData%\XIVLauncher\addon\Hooks\dev\`）を解決するため、追加の参照設定は不要。

## ゲームへのインストール（dev plugin として読み込ませる）

### 方法 1：XIVLauncher の dev plugin location を指定

1. ゲーム中に `/xlsettings` を実行（または XIVLauncher Setup → Dalamud Settings）
2. 「**Dev Plugin Locations**」タブを開く
3. ビルド出力ディレクトリ（例：`C:\Users\<user>\Documents\GitHub\ffxiv_echoes\src\FfxivEchoes\bin\x64\Debug\FfxivEchoes\`）を追加
4. `/xlplugins` で「FFXIV Echoes」を有効化

### 方法 2：手動コピー

`%AppData%\XIVLauncher\devPlugins\FfxivEchoes\` を作成し、ビルド出力をコピー。

## ホットリロード

1. ゲーム中に `/xlplugins` を開き、プラグインを **Disable**
2. ローカルで `dotnet build` を再実行
3. プラグインを **Enable**

ビルド出力先が dev plugin パスに直結している場合、Disable → Enable だけで最新ビルドがロードされる。
（dev plugin はファイルロックがかからないため、ビルド中に削除エラーが出る場合は一度 Disable してから build）。

## 起動確認

1. 起動後、ゲーム内チャットで `/echoes` を入力
2. メインウィンドウ（M1 setup verification ウィンドウ）が開けば成功
3. プラグインインストーラ画面（`/xlplugins`）の「FFXIV Echoes」エントリから設定ボタンも動くこと

## ログの見方

```
/xllog
```

でゲーム内ログウィンドウを開き、`[FfxivEchoes]` で grep。プラグイン側からのログは `Plugin.Log.Information(...)` 等で出力している（`Plugin.cs` 参照）。

## トラブルシューティング

### `error : The SDK 'Dalamud.NET.Sdk/15.0.0' specified could not be found.`

NuGet 経由で SDK の取得に失敗している。`dotnet restore` を再実行。それでも駄目なら：

```bash
dotnet nuget locals all --clear
dotnet restore
```

### `Dalamud assembly not found`

XIVLauncher がインストールされていない、または 1 回もゲームを起動していない可能性。XIVLauncher を起動 → ゲームを 1 回起動して Dalamud をブートさせると `%AppData%\XIVLauncher\addon\Hooks\dev\` に必要 DLL が配置される。

### `error CS0246: The type or namespace name 'Dalamud' could not be found`

`Dalamud.NET.Sdk` が機能していない。csproj の SDK 行が `<Project Sdk="Dalamud.NET.Sdk/15.0.0">` になっているか確認。

### ホットリロード後にプラグインが動かない

`/xlplugins` で Disable → Enable をやり直す。それでも駄目ならゲームクライアント自体を再起動。

## 関連

- [SPEC.md](../SPEC.md) §1.5 動作環境
- [roadmap.md](../roadmap.md) MVP / M1〜M10
- [.claude/rules/coding-rules.md](../.claude/rules/coding-rules.md) C# / Dalamud コーディング規約
- [Dalamud Plugin Development Docs](https://dalamud.dev/)
- [SamplePlugin（公式テンプレート）](https://github.com/goatcorp/SamplePlugin)
