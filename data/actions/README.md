# data/actions/

このディレクトリには、各ゾーンで登場する FFXIV Action の Lumina dump をコミットする。

## 生成方法

実機 Dalamud から `/echoes dump-actions [zone]` を 1 度だけ実行する。
zone を省略すると、trigger ファイル / 録画が存在する全 zone について dump する。

- 入力：`%AppData%/XIVLauncher/pluginConfigs/FfxivEchoes/triggers/<zone>.json`
  + `%AppData%/XIVLauncher/pluginConfigs/FfxivEchoes/recordings/<zone>/*.jsonl`
- 出力：`data/actions/<zone>.json`（このディレクトリ）

出力先はコマンド側で「リポジトリのルート（`FfxivEchoes.sln` 同居）」を自動検出する。
見つからない場合は ConfigDirectory/data/actions/ にフォールバックする。

## なぜリポにコミットしてよいのか

- Square Enix のゲームデータの一部だが、Lumina dump は **gimmick 半径・cast_type・omen** といった
  「攻略表示のために必要な最小サブセット」だけを抜き出した派生データであり、再頒布制限上の問題は
  プロジェクト方針として許容範囲とする（同種の dump は ACT 系プロジェクトでも常用されている）。
- 個人を特定できる情報は一切含まない。
- ファイルサイズが小さく（数 KB〜数十 KB）、リポに置いて headless replay（`FfxivEchoes.Replay`）で
  Dalamud SDK 非依存に再生できることのメリットが大きい。

## スキーマ

```jsonc
{
  "zone": "月の底",
  "generated_at": "2026-05-24T...",          // UTC ISO 8601
  "source_action_count": 42,                  // trigger + 録画で見つかった action_id 数
  "actions": [
    {
      "id": "0x67BF",
      "id_dec": 26559,
      "name": "パラデイグマ",
      "cast_type": 7,                         // Lumina CastType（2=Circle, 3=Cone, ...）
      "effect_range": 0.0,                    // メートル
      "x_axis_modifier": 0.0,                 // rect/cone の半幅相当
      "omen": null,                           // Omen row id（string 化）。0 のときは null
      "cast_time_ms": 5000,                   // インスタント = 0
      "is_player_action": false               // Lumina IsPlayerAction
    }
  ]
}
```

詳細は [docs/superpowers/specs/2026-05-24-aoe-fix-and-browser-replay-design.md §5](../../docs/superpowers/specs/2026-05-24-aoe-fix-and-browser-replay-design.md) を参照。

## サンプル

`_sample-月の底.json` は手書きの推測値サンプル。実機で `/echoes dump-actions 月の底`
を実行して上書きする想定。Replay harness 開発時の placeholder として使う。
