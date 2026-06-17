# 絶ケフカ AoE 表示「全然だめ」の根本原因と対応（2026-06-11）

## 症状

俯瞰図の AoE 事前描画（UpcomingAoePreviewService）をゲーム内テストしたところ、絶ケフカ（被検世界「シグマ」V4.0）でほぼ何も表示されなかった。

## 根本原因（証拠付き）

1. **形状解決のデータ源が両方空**
   - safe_call_dictionary（75 件）にケフカの cast_id が 0 件（辞書は 5/29 更新＝ケフカ実装前の他コンテンツのみ。当日録画の 22 種の cast_id と突合して一致 0）
   - Lumina にボスキャストの AoE 情報が無い（dalamud.log: 「skip ばりばりルインガ (0xC403) — Lumina に AoE 情報無し / 辞書 override 無し」）
   - ほぼ全キャストが self-target（target_id == source_id）で、確定側 AutoTelegraph も全て「skip self-target cast」
2. **オブジェクト AoE 経路がゾーン設定で全停止**
   - `AutoAoeDisplayPolicy.ShouldDrawObjectGroup` の先頭ゲートが `show_auto_telegraphs`（このゾーンで false）に従属し、ユーザーが明示した手動 object_aoe_rules まで殺していた

## 本質

絶ケフカの AoE は「ボスの詠唱 → Lumina 形状」ではなく、**無名グラウンドオブジェクト（EventObj）の出現**として現れる。録画には `object_appear`（data_id・位置付き）として記録済み。

## 録画解析で確定した事実（録画 8 本ベース）

| data_id | 正体 | 根拠 |
|---------|------|------|
| 2015266 | **氷床（ひろげるブリザガ）** | 詠唱→出現の相関 24 回（詠唱解決 ≈6s 後に 4 個同時出現） |
| 2015267 | ずびずばテレポ直後（t≈160）に複数出現する床（正体確認中） | サンダガとの相関 0。テレポ/トラップ系の可能性 |
| 2015154/55/63/64/65 | 不明（各 2x 出現） | 発見用マーカーで可視化して特定する |

- もりもりサンダガは床オブジェクトを**出さない**（拡大型 AoE の可能性）
- なぞなぞマジックは単一 cast_id（0xBA94）→ 真偽はなぞなぞ自体の ID では区別不可
- **裁きの光に 3 変種（0xC622 / 0xBABD / 0xBAE1）** → 真偽（正誤）の現れの最有力候補。次の分析対象
- action_used の target_x/z は -0.015 プレースホルダが多く、ダメージ位置からの形状推定には使いにくい（既知問題）

## 適用した対応

1. **ゾーン JSON に object_aoe_rules を追加**（ユーザーデータ。triggers_backup/ にバックアップ済み）
   - 氷床 2015266: circle r5m 30s #66CCFF
   - 床?267 / 床?154 等: 発見用の小マーカー
   - 追加スクリプト: `tools/add_kefka_object_rules.py`（冪等）
2. **`4ad1e72` fix(objects)**: 手動ルールを show_auto_telegraphs ゲートから分離（hasManualRule）。手動ルールは auto OFF でも 1 体から描画
3. **`df3b047` feat(hud)**: 自分デバフ一覧 HUD（名前・残り秒・スタック、残 5 秒未満は赤）。データ源は LocalPlayer.StatusList の直接参照

## 次のステップ（真偽＝踏む/踏まない）

ユーザー確認済み: 真偽は「なぞなぞマジックの正誤」で決まる。実装方針:
1. 裁きの光 3 変種と直後の挙動（被魔法ダメージ増加の付与有無・床の生滅）の相関を録画から分析し、正誤の機械判定シグナルを特定
2. 判定確定後、氷/雷床の塗り分け（踏め=緑/踏むな=赤）+ TTS「氷踏め」等
3. 床の事前表示は PredictedObjectSpawnLearner（cast→object 出現の学習）が既存。氷床は出現位置が安定なら事前描画可能

## 留意

- タイムライン連動の予測 AoE レイヤ（UpcomingAoePreviewService）は汎用コンテンツでは機能するが、絶ケフカのボスキャストには形状データ源が無いため出ない（仕様上の限界）。ケフカでは object_aoe_rules 経路が主役
- show_floor_paint もこのゾーンで false（床塗りは出ない。俯瞰図のみ）
