# タイムライン・AoE 実戦レベル監査レポート

- 日付: 2026-05-29
- 方法: 多角アドバーサリアル監査（Workflow, 47エージェント / 6サブシステム × レビュー→反証検証→統合）
- 検証: 40 findings 中 **38件が実在(isReal=true)** と確認
- 対象ブランチ: fix/recording-version-gate (= feature/minimap-boss-roles + 録画版ゲート修正)

## 総合評価: usable-with-caveats（条件付きで実用可）

### タイムライン予測
技名＋残り時間の事前 TTS/overlay は実際に機能（`PredictAdvanceWarningSec` 既定12s、`PredictedCastReminderService` は `BranchResolvedEvent` で再スケジュール、`UpcomingEventsWindow` はキャッシュ無効化）。価値提案の半分は成立。**ただし分岐コンテンツでは P0-1 未修正だと信頼不可。** 「音声/テキスト予告ツール」としては概ね使えるが、安置の事前可視化（予測描画）は dead code 化していて機能していない。

### AoE範囲描画
タイムラインより精度懸念が深い。**形状ジオメトリが権威データ(Lumina)でなく近似に依存**している箇所が分散。誤発火対策（self-target/raid-wide 0.9ガード/CastType6,7除外/(0,0)placeholder除外/EffectRange50m上限）は機能しており「誤った位置に出す」より「出ない/形が違う」方向＝安全側に倒れている。だが幅・角度・矩形投影・複数直線は実際にプレイヤーを危険へ誘導しうる。**現状は手動キュレーション済みの技のみ信頼でき、自動テレグラフ任せは精度不足。**

## 🔴 P0（実戦投入の障害）

| # | 問題 | 根拠 |
|---|---|---|
| P0-1 | **分岐後の事前TTSが永久に出ない**。`NoteReminderService` が `BranchResolvedEvent` を未購読（51-57行は CombatStarted/Ended/ZoneChanged のみ）。branch_id付き mechanic の先行警告が沈黙。さらに branch_id かつ advance_warning_sec>0 では `PredictedCastReminderService` が空アクションで NoteReminder へ委譲（140-142行）し事前警告が完全欠落。絶/滅のランダム分岐で沈黙する最も気付きにくい壊れ方 | `NoteReminderService.cs:51-57`。修正は `PredictedCastReminderService.cs:69` と同じ1行購読追加 |
| P0-2 | **複数直線AoEが単一30°扇に潰れる**。もりもりサンダガ型（ケフカ録画17本でほぼ毎回・300+回出現）。実態4〜8本の独立直線が `direction=N` の単一coneに集約。録画スキーマに caster heading が無く方向復元不可 | `TriggerAutoGenerator`(cast_id単位集約+direction=Nハードコード)、`AutoSafeCallPlanner`(CastType=4→cone/fan30) |

## 🟠 P1（重要）

| # | 問題 | 根拠 |
|---|---|---|
| P1-3 | 全直線AoEが固定半幅5m(幅10m)。Lumina `XAxisModifier`(実半幅)を読みながら `AoeInfo` で破棄 | `AoeResolver.cs:22-27`, `ActorTrackedAoeService.cs:866`(DefaultLineHalfWidthM=5.0) |
| P1-4 | 扇AoE角度が常に90°固定。実角度を推定せず、新ボス技ほど不正確。推定であることも非表示(IsEstimate=false) | `AutoSafeCallPlanner.cs:382/423`, `ActorTrackedAoeService.cs:573/845` |
| P1-5 | 矩形アリーナで円AoEが短辺方向に最大2倍ズレ、安置内外を反転しうる。ソース起点投影(単一スカラー)とプレイヤー投影(halfX/halfZ軸独立)の正規化方式が分裂 | `ArenaProjection.cs:19-20 vs 45-46` |
| P1-6 | 学習データが古いまま残存(ケツァクウァトルdonut radius=15、実6m)。コード修正(07541ab)済みでも再学習しない限り過大AoEで中央安置を危険誤誘導。oversizeガード18mが15mを弾かない | 本番 `月の底.json` の object_aoe_rules |
| P1-7 | 学習/reconcileが手動コマンド依存で起動時/戦闘終了時に自動配線されていない(設計§3.3 A/C未配線)。「録画すれば次回自動」が最も録画したケフカで不成立 | `Plugin` のLearnFromRecordings配線欠落 |
| P1-8 | `MultiCastDetector` が任意の同時発火を無条件 two_side_cleave/前後安置 と誤ラベルし辞書汚染。occurrence無視のフラット共起カウント | `MultiCastDetector` |

## 🟡 P2（改善）

- 事前警告秒(既定12s)が全予測に一律。cast_time が `RecordingAggregationReader.CastKey` で破棄され技ごと調整不能（long-cast早すぎ/連続技溜まる）
- `PredictedObjectSpawnService` が `CastCanceledEvent` 未購読→キャンセル/分岐したcastの予約済み予告AoEが消えない
- occurrence固定6秒窓で後半フェーズが複数occurrenceに分裂しConfidence不当低下
- CastType3の扇が DrawGimmickBody と DrawActualAoeShape で二重描画(`MinimapWindow.cs:458`のスキップ条件にType3欠落)
- 分岐判別を window_sec(30s)内に観測できないと branch が永久Pending固定→分岐mechanicが戦闘終了まで全非表示(フォールバック/警告ログ無し)
- 同時多発AoE時に固定窓高+NoResizeで2-3枚目タイルと「次→」calloutが下端クリップ

## 安置の事前可視化が dead code
`TryDrawPredictedMarker`/`TryEmitPredictedFloorPaint` が呼び出し0件。安置の形・方向は実cast発動後にしか出ない。SPEC(`docs/current-spec.md:263`)と矛盾。

## ✅ 強み
- 誤発火を安全側（出ない/形違い）に倒す多層ガード（self-target/raid-wide/placeholder除外/EffectRange上限）。「存在しない安置を信じさせる」最悪を構造的に回避
- 手動キュレーション経路は著者指定の HalfWidthM/InnerRadiusM/fan_deg を尊重
- ライブ方向解決が録画固定値でなく実ゲーム状態(actor.Rotation)を読む。ボス向き依存ギミックはライブで追従
- 例外境界が主要サービス＋EventBusに入りフレームループへの伝播を防止
- 既存調査ノート＋録画ログ駆動検証(recordings/*.jsonl の実position値で裏取り)のデバッグ文化が機能

## 推奨着手順（優先度順）
1. **[P0-1]** `NoteReminderService` に `bus.Subscribe<BranchResolvedEvent>(_ => Schedule())` を1行追加＋Dispose破棄。回帰テストで「branch Pending→Active後に branch_id付き advance_warning mechanic が _pending に入る」を検証
2. **[P0-2]** multi-caster同時直線を source_id 単位で個別zone化。当面cone→複数lineに手動修正しdirection=Nを外す。恒久対策は録画スキーマ(`CastStartedEvent`/`EventSerializer`)に caster rotation/heading を追加
3. **[P1]** `AoeResolver.AoeInfo` に XAxisModifierM 追加＋HalfWidth解決に流す／扇角度のOmen→角度bucketマップ＋IsEstimate表示／`ArenaProjection.ProjectWorldToMap` を軸独立(楕円)に統一＋回帰テスト／学習reconcileを戦闘終了時・起動時に自動配線＋旧形式spawn.Id再採番＋乖離>2m警告／MultiCastDetectorの固定ラベル廃止
4. **[P2]** cast_time伝播でper-trigger警告秒／CastCanceled購読／occurrence序数対応付け／CastType3二重描画修正／分岐Pendingフォールバック＋警告／ミニマップAlwaysAutoResize
5. **[継続]** SPECを実態に合わせ修正、予測描画dead codeを削除or安全に復活
