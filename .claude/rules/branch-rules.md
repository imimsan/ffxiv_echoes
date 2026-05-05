# ブランチ運用ルール

このプロジェクトでは GitHub Flow を変形した 3 階層モデルを採用する。
**ユーザー（リポジトリオーナー）からの明示的な指示なしに、Claude が以下の禁止事項を破ってはならない。**

---

## ブランチ階層

```
main          ←  リリース可能な安定版のみ
  ↑
develop       ←  次回リリースに向けた統合ブランチ
  ↑
feature/*     ←  機能・タスク単位の作業ブランチ（M1, M2 ... のような単位）
fix/*         ←  バグ修正ブランチ
chore/*       ←  ドキュメント・設定・雑務など実装を伴わない変更
```

---

## ブランチごとの絶対ルール

### `main` ブランチ

- **PR 作成禁止**：ユーザーの明示的指示があるまで `main` への PR を作成してはならない。
- **マージ禁止**：いかなる場合も `main` への直接マージ／PR マージを行ってはならない（ユーザー明示指示がない限り）。
- **直接 push 禁止**：`git push origin main` 相当の操作は禁止。
- **force push 禁止**：`--force` / `--force-with-lease` を含む強制 push は絶対禁止。
- リリースタグはユーザーが手動で打つ。Claude は提案のみ。

### `develop` ブランチ

- **PR 作成は許可**：`feature/*` や `fix/*` から `develop` への PR は Claude が作成してよい。
- **マージは禁止**：ユーザーの明示的指示があるまで PR をマージしてはならない（GitHub UI 上のマージ、`gh pr merge`、`git merge` すべて含む）。
- **直接編集は推奨されない**：原則 feature ブランチ経由で PR を出す。ただし、ブランチ初期化や軽微な復旧などのブートストラップ操作で `git push origin develop` が必要な場合は許可（一般的な push は `git push:*` が `ask` 扱いでプロンプトが出るので過剰な誤操作を防ぐ）。
- **rebase / squash の選択もユーザー判断**：マージ戦略を Claude が独断で決めない。

### `feature/*` `fix/*` `chore/*` ブランチ

- 自由に作成・編集・push してよい。
- **必ず `develop` から派生**させる。`main` から直接派生させない。
- 命名規則：
  - `feature/<roadmap-id>-<short-name>` 例：`feature/m1-dev-environment`
  - `fix/<short-description>` 例：`fix/cast-id-overflow`
  - `chore/<short-description>` 例：`chore/update-gitignore`
- 1 機能 = 1 ブランチ。スコープが膨らんだら分割する。
- 作業終了後は `develop` への PR を作成する。

---

## 禁止操作チェックリスト

以下のコマンドはユーザーの明示的指示がない限り Claude は実行禁止：

- [ ] `git push origin main`
- [ ] `git push --force` / `git push -f`（任意のブランチに対して）
- [ ] `git merge` を `main` または `develop` のチェックアウト中に実行
- [ ] `gh pr merge`（任意のブランチに対して）
- [ ] `gh pr create --base main`
- [ ] `git branch -D <branch>`（ローカルブランチの強制削除）
- [ ] `git push origin --delete <branch>`（リモートブランチ削除）
- [ ] `git reset --hard` でユーザーの作業を上書きする操作
- [ ] `git rebase` で push 済みのコミットを書き換える操作

ユーザーが上記を指示した場合のみ実行する。指示の範囲（一回だけか継続かなど）も確認する。

---

## 通常ワークフロー

1. ユーザーから作業指示（例：「M2 を実装して」）
2. Claude は `develop` から `feature/m2-plugin-framework` を作成しチェックアウト
3. 実装・テスト・コミット
4. `git push -u origin feature/m2-plugin-framework`
5. `gh pr create --base develop` で PR を作成
6. **ここで停止**。Claude は PR をマージしない。ユーザーがレビュー・マージを行う。
7. ユーザーから「`develop` を `main` にマージして」と指示があった場合のみ、`main` への PR 作成を行う（マージはやはりユーザーが行う）。

---

## 例外規定

このルールに反する操作が必要だと Claude が判断した場合は、**実行する前に必ずユーザーに確認**する。
「想定外の状態だから」「効率のため」などの理由で独断実行してはならない。

参考：`.claude/rules/git-rules.md`（コミットメッセージ・PR 形式など）
