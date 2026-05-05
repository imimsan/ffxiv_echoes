# .claude/rules

このディレクトリは ffxiv-echoes 開発における運用ルールをまとめる。Claude Code が作業時に参照するための場所であり、人間の開発者にとっても参考資料として機能する。

## ファイル一覧

| ファイル | 内容 |
|----------|------|
| [branch-rules.md](./branch-rules.md) | ブランチ階層と main/develop/feature の運用ルール、PR・マージ禁止事項 |
| [git-rules.md](./git-rules.md) | コミットメッセージ形式、PR テンプレート、履歴管理ルール |
| [coding-rules.md](./coding-rules.md) | C# / Dalamud コーディング規約、命名・フォーマット・設計指針 |
| [security-rules.md](./security-rules.md) | 機密情報の扱い、破壊的操作の制限、規約遵守事項 |

## 優先順位

ルール間で矛盾が発生した場合の優先順位：

1. `security-rules.md`（安全性が最優先）
2. `branch-rules.md`（リポジトリ整合性）
3. `git-rules.md`（履歴整合性）
4. `coding-rules.md`（コード品質）

矛盾を発見したら、ユーザーに確認の上ルールを更新する。

## 適用範囲

- リポジトリ直下のすべての作業に適用される。
- 自動的に Claude のコンテキストに読み込ませるため、リポジトリ直下の `CLAUDE.md` から参照している。
- `.claude/settings.json` の `deny` ルールは機械的に強制される。本ドキュメント群は人間（と Claude）が読んで判断する規約。

## 更新時のフロー

1. 提案を `chore/update-rules-<topic>` ブランチで行う。
2. PR で議論。
3. `develop` にマージ後、必要に応じて `main` へ伝播。
