# Git 運用ルール

ブランチ運用は `branch-rules.md` を参照。本ファイルはコミット・PR・履歴管理のルールを定める。

---

## コミット

### コミットメッセージ形式（Conventional Commits 準拠）

```
<type>(<scope>): <subject>

<body>

<footer>
```

- **type**（必須）：以下のいずれか
  - `feat`：新機能（roadmap の M / F / P 項目に対応する実装）
  - `fix`：バグ修正
  - `docs`：ドキュメントのみの変更
  - `style`：コードの動作に影響しない変更（フォーマット・コメントなど）
  - `refactor`：機能変更を伴わない構造改善
  - `perf`：パフォーマンス改善
  - `test`：テスト追加・修正
  - `chore`：ビルド・依存・設定など
  - `revert`：以前のコミットの取り消し
- **scope**（任意）：影響範囲。例：`recorder`, `trigger-engine`, `ui`, `m1`
- **subject**（必須）：50 文字以内の要約。日本語可。命令形・現在形（「した」より「する」）。
- **body**（任意）：「なぜ」を書く。「何をしたか」は diff を見れば分かる。
- **footer**（任意）：破壊的変更（`BREAKING CHANGE:`）、関連 issue 参照（`Refs: #12`）など。

### 例

```
feat(m1): Dalamud SDK のセットアップとプロジェクト初期化

SamplePluginを土台に、ffxiv-echoes プラグインのプロジェクト構造を作成。
ビルドとホットリロードの動作を確認。

Refs: roadmap.md M1
```

```
fix(trigger-engine): cast_id の 16 進パース時のオーバーフロー対策

uint で受けていた箇所を ulong に変更。0xFFFFFFFF を超えるカスタム ID
が将来追加される可能性を考慮。
```

### コミット粒度

- **1 コミット = 1 論理変更**。レビューで読める単位に分割する。
- WIP コミットは `git commit --amend` でまとめてから push する（push 後の amend は禁止）。
- フォーマット変更と機能変更を同一コミットに混ぜない。

### 禁止事項

- `git commit --no-verify` でフックをスキップしない（フックエラーは原因を解消する）。
- **コミット署名のスキップ禁止**：`--no-gpg-sign` などの署名回避フラグを使用しない。
- **push 済みコミットの書き換え禁止**：`git rebase -i` / `git commit --amend` で履歴を書き換えるのはローカル未 push 分のみ。
- 機密情報（API キー・トークン・パスワード）を含むコミットは作成しない（`security-rules.md` 参照）。

---

## プルリクエスト

### PR タイトル

コミットメッセージと同じ形式：
```
<type>(<scope>): <subject>
```

### PR 本文テンプレート

```markdown
## 概要
<このPRで何を達成するか。1〜3行>

## 関連
- roadmap: M1 / F2 / P3 など
- issue: #12（あれば）

## 変更内容
- [ ] 実装
- [ ] テスト
- [ ] ドキュメント

## 動作確認
<どう確認したか。手順・スクリーンショット・ログなど>

## レビュー観点
<レビュアーに見てほしい箇所があれば>

## チェックリスト
- [ ] ビルドが通る
- [ ] テストを追加または更新した
- [ ] `coding-rules.md` に従っている
- [ ] 機密情報を含まない
```

### PR の規模

- 1 PR = 1 機能単位（roadmap の M/F/P 1 項目程度）。
- 500 行を超えそうな場合は分割を検討。
- 巨大な機械的変更（フォーマット・リネーム）は別 PR に分離。

### マージ戦略

- `feature/*` → `develop`：**Squash and merge**（履歴をクリーンに保つ）
- `develop` → `main`：**Merge commit**（リリース単位を保持）
- いずれもマージ実行はユーザー判断（`branch-rules.md` 参照）。

---

## タグ・リリース

- バージョニングは [Semantic Versioning](https://semver.org/lang/ja/) 準拠：`vMAJOR.MINOR.PATCH`
- MVP 完了 = `v0.1.0`、フェーズ 2 完了 = `v0.2.0`、絶妖星乱舞対応版 = `v1.0.0` を想定。
- タグ作成はユーザーが手動で行う。Claude は提案のみ。

---

## 履歴を破壊する操作の絶対禁止

以下はユーザーの明示的指示が**毎回必要**。指示があってもさらに確認する：

- `git reset --hard` で他人（ユーザー）の作業を巻き戻す
- `git push --force` / `git push --force-with-lease`
- `git rebase` で push 済みコミットを書き換え
- `git branch -D` で未マージブランチを強制削除
- `git filter-branch` / `git filter-repo`
- `gh pr close` で他者作成 PR を閉じる

---

## .gitignore メンテナンス

- 新しいツール導入時に必ず `.gitignore` を更新する。
- 一時ファイル・IDE 設定・ローカルログ・ビルド成果物は必ず除外。
- 既にコミット済みのファイルを ignore する場合は `git rm --cached` を使う（ユーザーに事前確認）。

---

## サブモジュール・外部依存

- Dalamud SDK は NuGet（`Dalamud.NET.Sdk`）または `DALAMUD_HOME` 環境変数で参照する。リポジトリにベンダー化しない。
- サードパーティライブラリは NuGet 管理。`packages.lock.json` をコミットする。

---

## 参照

- `.claude/rules/branch-rules.md` — ブランチ階層と禁止操作
- `.claude/rules/coding-rules.md` — コーディング規約
- `.claude/rules/security-rules.md` — 機密情報の扱い
