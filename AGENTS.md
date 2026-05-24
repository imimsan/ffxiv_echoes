# AGENTS.md — ffxiv-echoes

このファイルは Codex がセッション開始時に自動で読み込むプロジェクト指示書。

## プロジェクト概要

FF14（ファイナルファンタジーXIV）の絶コンテンツ攻略支援プラグイン。Dalamud 上で動作する C# / .NET プラグイン。詳細は [SPEC.md](./SPEC.md) と [roadmap.md](./roadmap.md) を参照。

## 言語

- ユーザーとのやりとりは **日本語**。
- コミットメッセージ・PR タイトルも日本語可。
- コード内の識別子（クラス名・変数名）は **英語**。
- コメントは日本語可（書く必要があるときのみ）。

## ルールファイル（必読）

作業前に必ず以下のルールに目を通すこと。矛盾は `security` > `branch` > `git` > `coding` の優先順。

- [.Codex/rules/branch-rules.md](./.Codex/rules/branch-rules.md) — ブランチ運用（**main / develop の禁止事項あり**）
- [.Codex/rules/git-rules.md](./.Codex/rules/git-rules.md) — コミット・PR ルール
- [.Codex/rules/coding-rules.md](./.Codex/rules/coding-rules.md) — C# / Dalamud 規約
- [.Codex/rules/security-rules.md](./.Codex/rules/security-rules.md) — 機密情報・破壊的操作の制限

## 重要な絶対ルール（要約）

ユーザーの明示的指示なしに以下を実行してはならない：

1. `main` ブランチへの PR 作成 / マージ / 直接 push
2. `develop` ブランチへの PR マージ（PR 作成は OK）
3. `git push --force` / `--force-with-lease`
4. `rm` / `Remove-Item -Recurse -Force` などの破壊的削除
5. `.env` / `.env.*` の読み込み
6. `git commit --no-verify`（フックスキップ）
7. 履歴改変（`git rebase` で push 済みコミット書き換えなど）

詳細は各ルールファイル参照。`settings.json` の `deny` で機械的に拒否される項目もある。

## 開発ワークフロー

```
main ── develop ── feature/m1-dev-environment
                ── feature/m2-plugin-framework
                ── ...
```

- 機能単位で `feature/<roadmap-id>-<short-name>` を `develop` から派生
- 完了したら `develop` に PR 作成（マージはユーザー判断）
- リリース時にユーザー指示で `develop` → `main` へ PR

## ロードマップ進行状況

現在地は [roadmap.md](./roadmap.md) を確認。マイルストーンは M1（環境構築） → M2 〜 M10（MVP） → F1 〜 F12（フェーズ2） → P1 〜 P6（フェーズ3）。

絶妖星乱舞（2026年6月2日実装予定）までに MVP + フェーズ2 完成が目標。

## ファイル参照

- [SPEC.md](./SPEC.md) — メイン仕様書
- [roadmap.md](./roadmap.md) — 実装ロードマップ
- [docs/dev-setup.md](./docs/dev-setup.md) — 開発環境構築（M1）の手順
- [trigger-schema.json](./trigger-schema.json) — トリガー定義のJSONスキーマ
- [極エヌオー討滅戦.json](./極エヌオー討滅戦.json) — サンプルトリガー（基本）
- [絶妖星乱舞.json](./絶妖星乱舞.json) — サンプルトリガー（高度）
- [sample-recording.jsonl](./sample-recording.jsonl) — 録画ログサンプル

## プロジェクト構成（M1 時点）

- `FfxivEchoes.sln` — ソリューション
- `src/FfxivEchoes/FfxivEchoes.csproj` — プラグイン本体（`Dalamud.NET.Sdk/15.0.0`）
- `src/FfxivEchoes/FfxivEchoes.json` — Dalamud プラグインマニフェスト
- `src/FfxivEchoes/Plugin.cs` — エントリポイント（`IDalamudPlugin` 実装）
- `src/FfxivEchoes/Configuration.cs` — `IPluginConfiguration` 実装
- `src/FfxivEchoes/Windows/` — ImGui ウィンドウ群

スラッシュコマンド：`/echoes` でメインウィンドウを開く。
