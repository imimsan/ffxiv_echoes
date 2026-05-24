# 作業引き継ぎメモ

最終更新：2026-05-06（セッション末尾、コンテキスト圧縮済み）
ブランチ：`develop`（コミット未済の変更あり）

---

## 1. 現在のリポジトリ状態

### 直近にマージ済み（develop）
直近 5 件：
```
a2c376c feat(demo,timeline-notes): デモから center overlay 削除 + アイコン複数枚対応 (#31)
3b8b55f feat(demo): WEB デモ（タイムライン / オーバーレイをブラウザで確認）   (#30)
9f992ec feat(timeline-notes): タイムラインに「ここで軽減」等のノートを表示・通知 (#29)
4e3548c feat(p6): オーバーレイアニメーション（フェード + パルス）              (#28)
a14a27c feat(p5): カスタムスクリプト基盤（スタブ）                              (#27)
```

MVP（M1〜M10）+ フェーズ 2（F1〜F12）+ フェーズ 3（P1〜P6）+ Timeline Notes + Web Demo まで全 31 PR が squash-merge 済み。

### 未コミット（develop に直接ある作業ツリー上の変更）

```
M demo/demo.js            (+115 行)
M demo/index.html         (+5 行)
M demo/sample-config.json (+27 行 / -7 行)
M demo/style.css          (+53 行)
```

これは **デモへのアリーナ図（俯瞰 SVG）追加** の作業。動作確認済み・PR 未作成・コミット未作成。

未追跡のスクリーンショット類（PR 用ではない、デモ動作確認時に撮ったもの）：
```
demo-busy-timeline.png / demo-cast-fired.png / demo-center-issue.png
demo-full-overlay.png  / demo-initial.png    / demo-overlay-active.png
demo-overlay-text.png  / .playwright-mcp/    / AGENTS.md
```
→ これらはコミットしない方針。

---

## 2. 今セッションで完成させたこと（未コミット）

### デモ：右上アリーナ図（top-down SVG）

ボスキャストが発生したとき、ゲームエリア右上に俯瞰のアリーナ図がポップアップする。

**5 つの gimmick タイプ**
| type | 表示 | 例（無の肥大 = outer_ring） |
|---|---|---|
| `outer_ring`   | 外周赤、中央に緑の安置丸 + "SAFE" | 中央安置（外周回避） |
| `inner_circle` | 中央赤円、外周は通常 | 外周安置（中央 AoE） |
| `scatter`      | 中央 ! マーク + 4方向（N/E/S/W）青丸 | 4方向散開 |
| `stack`        | 中央青の集合丸 + "STACK" | 中央集合 |
| `cone`         | 指定方向への扇形（`direction` + `fan_deg`） | 北方向、南へ回避 |

**変更ファイル（4 つ）**
- `demo/style.css` — `.arena-view` / `.arena-svg` / `.arena-callout` + `arena-pop` キーフレーム
- `demo/demo.js` — `renderArena()` + `renderArenaSvg()`、`tick()` でフレーム毎呼び出し、`stopCombat`/`resetAll` でクリア
- `demo/sample-config.json` — `_demo_boss_casts[]` に `gimmick` フィールド追加（5 種類すべての例を含む）
- `demo/index.html` — 凡例と「使い方」更新、アリーナ用 div 追加

**動作確認方法**
```bash
cd demo && python -m http.server 8765
# http://localhost:8765/ を開いて「戦闘開始」
```
t=8（外周回避）→ t=25（散開）→ t=45（外周安置）→ t=80（集合）→ t=120（コーン）と順に出る。

---

## 3. 次にやりたいこと

### A. デモのアリーナ図変更を PR 化

**ブランチ案**：`feature/demo-arena-view`

**コミット粒度**：1 コミットで OK（4 ファイル、+200 行のデモのみ追加）。

**コミットメッセージ案**：
```
feat(demo): アリーナ俯瞰図でギミック安置を可視化

5 種類の gimmick type（outer_ring / inner_circle / scatter / stack / cone）に対応。
ボスキャスト中だけ右上に表示し、安置を緑、危険を赤で描画。
```

### B. プラグイン本体に MinimapWindow を実装（メイン課題）

ユーザー要望：「実機でも同じような俯瞰図が欲しい」。

**現状のプラグイン側ウィンドウ**
- `OverlayWindow.cs`        — 中央テキスト + タイマーバー（実装済）
- `LiveTimelineWindow.cs`   — 下部のライブタイムライン（実装済）
- `WorldOverlayWindow.cs`   — 矢印 / フィールドマーカーをワールド座標に投影（実装済）
- `MainWindow.cs`           — 設定 UI（実装済）

**新設するもの**
1. `src/FfxivEchoes/Windows/MinimapWindow.cs` — ImGui の DrawList で SVG 相当を描画する新ウィンドウ
2. `src/FfxivEchoes/Actions/Handlers/ArenaViewHandler.cs` — 新しいアクションタイプ `arena_view` のハンドラ
3. `src/FfxivEchoes/Triggers/Models/ActionDefinition.cs` — `Gimmick`（string）+ `Callout`（string）フィールド追加
4. `src/FfxivEchoes/Triggers/Models/AutoSettings.cs` — `ShowMinimap`（bool）追加
5. `src/FfxivEchoes/Plugin.cs` — `MinimapWindow` と `ArenaViewHandler` を DI 登録 + `WindowSystem` 追加
6. `trigger-schema.json` — `arena_view` アクションの schema 追加

**設計メモ**
- API：`MinimapWindow.AddArenaView(gimmickType, callout, durationSec)` を `ArenaViewHandler` から呼ぶ。`OverlayWindow` の `AddText` / `AddTimerBar` と同じパターン。
- 描画：ImGui の `ImDrawList` には `AddCircle` / `AddCircleFilled` / `PathArcTo` / `PathFillConvex` があり、SVG とほぼ同等の描画ができる。dashed circle は短い弧の繰り返しで近似（または実線でも可）。
- 表示位置：ImGui の Window で固定サイズ（200x240px くらい）、初期位置は画面右上、ユーザーが移動可（`OverlayWindow` と同じ）。
- 表示条件：`activeGimmicks` が空なら `IsOpen = false`、何かあれば `IsOpen = true`。
- フィルタ：`auto_settings.show_minimap` が `false` なら一切描画しない。

**JSON 利用例（トリガー定義側）**
```json
{
  "id": "muno_higai_arena",
  "match": { "cast_id": "0x9D32" },
  "actions": [
    { "type": "tts", "text": "外周回避" },
    { "type": "arena_view", "gimmick": "outer_ring", "callout": "中央安置", "duration": 5 }
  ]
}
```

**ブランチ案**：`feature/plugin-minimap-window`

**PR 戦略の選択肢**
- (1) A → B の順で 2 PR に分割（推奨：レビュー単位が明確）
- (2) A + B を同じ PR にまとめる（理由：「アリーナ図」という機能単位で見ればワンセット）
- どちらでも OK。ユーザー判断。

---

## 4. 重要な参照ファイル

- 仕様：[SPEC.md](./SPEC.md)、[roadmap.md](./roadmap.md)
- 既存 ImGui ウィンドウのパターン：`src/FfxivEchoes/Windows/OverlayWindow.cs`（`Window` 継承 + `AddText` API + `Draw()` 内で DrawList 使用）
- アクションハンドラのパターン：`src/FfxivEchoes/Actions/Handlers/`（`IActionHandler` 実装、コンストラクタで依存注入）
- 既存の SafeZone 計算（将来的にミニマップに重畳できる）：`src/FfxivEchoes/SafeZone/`（16 種のプリセット）
- ルール（必読）：`.claude/rules/branch-rules.md` / `git-rules.md` / `coding-rules.md`

---

## 5. 開発ワークフロー（再掲）

```
develop から feature/<name> を派生
  ↓
実装 + コミット（1 コミット = 1 論理変更、Conventional Commits）
  ↓
git push -u origin feature/<name>
  ↓
gh pr create --base develop（PR 作成のみ）
  ↓
ここで停止。マージはユーザーが行う。
```

**禁止**：`main` への PR / マージ / 直接 push、develop への PR マージ、force push、`--no-verify`。

---

## 6. 引き継ぎ後の最初の一手（推奨）

1. このファイル（HANDOFF.md）を読む
2. `git status --short` で未コミット変更を確認
3. `git log --oneline -5` で直近マージ状態を確認
4. デモを起動して動作確認：`cd demo && python -m http.server 8765` → `http://localhost:8765/`
5. ユーザーに「A だけ先に PR 化する？それとも A + B 一括？」を確認
6. 確認とれたらブランチを切って実装開始
