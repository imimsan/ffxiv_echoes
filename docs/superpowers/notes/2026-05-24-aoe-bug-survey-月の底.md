# 月の底 AoE 表示バグ調査レポート
作成: 2026-05-24, by T1-recording-survey

---

## 1. AoE 描画フロー（コード理解）

### イベント起点とサービス責務

```
cast_start イベント
  ├─ AutoTelegraphService.OnCastStart
  │    Lumina.AoeResolver.Resolve() で形状を取得
  │    self-target / friendy / raidwide / CastType=6/7 でフィルタ後
  │    MinimapWindow.AddArenaView() で描画 （castTime+3秒）
  │    → ActorTrackedAoeService が毎フレーム位置追跡
  │
  └─ PredictedObjectSpawnService.OnCastStart
       spawn.TriggerCastId が一致する PredictedObjectSpawn を検索
       fireAfterSec = max(0, DelaySec - LeadTimeSec) 後にスケジュール
       スケジュール時刻到達 → FireSpawn() → TriggerFiredEvent publish
       → ArenaViewHandler が arena_view action を MinimapWindow に描画

cast_complete イベント
  ├─ AutoTelegraphService → (特に何もしない)
  └─ PredictedObjectSpawnService.OnCastComplete
       → spawn.TriggerEvent == "cast_complete" の spawn を同様にスケジュール

object_appear イベント
  ├─ AddObjectAoeService.OnAppear
  │    LookupAoe() でルール/辞書/録画から形状を引く
  │    group へ追加し、OnUpdate で発火条件チェック
  │    FireGroup() → member.EntityId で actor を ObjectTable から再取得
  │    → TriggerFiredEvent publish → MinimapWindow に描画
  │
  └─ PredictedObjectSpawnService.OnObjectAppeared
       pendingPredictions から data_id に対応する spawn を外す（dedup解除）

action_used イベント
  ├─ AutoTelegraphService.OnActionUsed
  │    target_world が (-0.015,-0.015,-0.015) ならスキップ
  │    AoeResolver.Resolve() → MinimapWindow.AddArenaView (3秒)
  │
  └─ AddObjectAoeService.OnActionUsed
       ソースが object NPC で recording_action 学習対象なら
       AoeResolver.Resolve() → PersistLearnedObjectRule() → キャッシュ更新

LiveScan (フレームレート毎):
  AddObjectAoeService.TryFireLiveObjectSnapshot()
    ObjectTable を直接走査 → placeholder 位置スキップ → LookupAoe() → FireGroup()
```

### 描画決定の主な条件分岐サマリ

| 経路 | 起点 | 位置ソース | 形状ソース |
|---|---|---|---|
| AutoTelegraph (cast) | cast_start | ObjectTable (source actor) または targetWorld | Lumina CastType |
| AutoTelegraph (action) | action_used | targetWorld / source actor | Lumina CastType |
| AddObjectAoe (appear) | object_appear | ObjectTable 再取得 or 出現時 pos | ルール/辞書/録画学習 |
| AddObjectAoe (livescan) | cast_start/complete/action_used | ObjectTable 直接走査 | LookupAoe() |
| PredictedObjectSpawn | cast_start / cast_complete | トリガー定義の静的座標 | トリガー定義形状 |

---

## 2. 症状別バグ

### 症状 A: 予告 AoE が出ない / 遅い

**頻度**: 17/18 録画に 0x67BF パラデイグマが存在（`2026-05-15_08-57-15.jsonl` のみパラデイグマ無し）

**該当 cast / spawn 設定**:
- cast: `0x67BF` パラデイグマ (ゾディアーク、cast_time=2.7秒)
- `spawn_67BF_3834` (data_id=14388): `delay_sec=13.411`, `lead_time_sec=5`
- `spawn_67BF_233C` (data_id=9020): `delay_sec=13.413`, `lead_time_sec=5`

**録画証拠（2026-05-15_10-05-17.jsonl）**:

```jsonl
行33: {"time":17.295,"type":"cast_start","cast_id":"0x67BF","cast_name":"パラデイグマ",...}
行53: {"time":32.208,"type":"object_appear","object_name":"ケツァクウァトル","data_id":14388,"position":{"x":89.5,"y":0,"z":89.5},...}
```

全17録画での計測値:
- cast_start: t ≈ 17.27〜17.35秒（±0.08秒）
- object_appear: t ≈ 32.17〜32.31秒（±0.14秒）
- 実際のDelayは約14.9〜14.97秒

**根本原因仮説 A-1: delay_sec の学習値が過小（最大の問題）**

`PredictedObjectSpawnService.FireSpawnsForEvent()` の計算:
```csharp
// PredictedObjectSpawnService.cs:169-171
var leadTime = Math.Clamp(spawn.LeadTimeSec, 0, spawn.DelaySec);
var fireAfterSec = Math.Max(0, spawn.DelaySec - leadTime);
ScheduleSpawn(profile, spawn, sourceEvent, fireAfterSec);
```

- `spawn_67BF_3834`: `fireAfterSec = 13.411 - 5.0 = 8.411秒` → cast_start後 8.4秒で発火 = t ≈ 25.7秒
- 実際のobject_appear = t ≈ 32.2秒（= cast_start後約14.9秒）
- LeadTime の予定: 32.2 - 25.7 = **6.5秒前**（設定LeadTime=5秒より早い → これは機能する）

計算上は `spawn_67BF_3834` は正しく動作するはずだが、**`delay_sec=13.411` が実際の遅延 14.9秒と約1.5秒ずれている**。`lead_time_sec=5` + `delay_sec=13.411` の合計は 18.411秒 < 実際の 14.9秒のため、`LeadTime`側は 5 秒をキープできているが、学習値 delay_sec と実測の差分（1.5秒）がある。

**根本原因仮説 A-2: spawn_id 重複による dedup 誤動作**

`月の底.json` の `predicted_object_spawns` に **同一の id `"spawn_67BF_233C"` が 3 エントリ存在**している（行 307, 351, 後続）。

```json
// 1つ目 (trigger_event="cast_start", delay=13.413, object_name="ケツァクウァトル" data_id=9020)
{"id":"spawn_67BF_233C","trigger_event":"cast_start","delay_sec":13.413,...}

// 2つ目 (trigger_event="cast_start", delay=18.747, object_name="ゾディアーク" data_id=9020)
{"id":"spawn_67BF_233C","trigger_event":"cast_start","delay_sec":18.747,...}
```

`PredictedObjectSpawnService.ScheduleSpawn()` は `s.Spawn.Id == spawn.Id` で dedup を行う（`PredictedObjectSpawnService.cs:182-185`）。同一 Id を持つ複数の spawn が FireSpawnsForEvent ループで処理されると、**最初に処理されたもの以外は dedup によりスキップされる**。

```csharp
// PredictedObjectSpawnService.cs:182-185
if (_scheduled.Any(s => s.Spawn.Id == spawn.Id &&
    (fireAt - s.FireAt).Duration().TotalSeconds < DedupWindowSec))
{
    return; // ← spawn_67BF_233C の 2〜3 件目がここでスキップ
}
```

`DedupWindowSec=5.0` のため、`delay_sec=13.413` と `delay_sec=18.747` の差は 5.334秒でぎりぎり dedup されない場合があるが、**id の重複自体が設計意図外**。

**根本原因仮説 A-3: data_id=9020 の変身体 spawn が混入**

`spawn_67BF_233C` (data_id=9020) の形状が `"circle", radius=5` 。これは変身前のゾディアーク add を学習したもので、本物のケツァクウァトル本体(14388)の `"donut", radius=6` とは別形状。しかし、`spawn_67BF_3834`（data_id=14388、donut、radius=6）の方が正しい予告対象なので、これが正しく発火していれば症状は出ないはず。

**ステュクス（0x67F3）の spawn 設定の問題**:

`spawn_67F3_3834` は `delay_sec=0.733` で `lead_time_sec=5` のため:
- `fireAfterSec = max(0, 0.733 - 5) = 0秒` → cast_start 検知と同時に発火

これはステュクスのcast_start時点ですでにケツァクウァトルがobject tableに存在している（32.2秒）→ステュクスcast_start 31.5秒 → ケツァクウァトルはすでに出現済み。予告ではなく確認表示となるが、`DedupWindowSec=5.0` によって既に fired 扱いになっていることも。

---

### 症状 B: 実 AoE の位置がおかしい

**頻度**: 18/18 録画でケラノウス・エイドロン(0x67E1)が placeholder 座標で発動

**録画証拠（2026-05-15_10-05-17.jsonl、行65〜68）**:

```jsonl
{"time":32.355,"type":"action_used","source":"ケツァクウァトル","source_id":1073794276,
 "action_id":"0x67E1","action_name":"ケラノウス・エイドロン",
 "auto_attack":false,"target":null,
 "target_x":-0.015,"target_y":-0.015,"target_z":-0.015}
```

全18録画で target_x/y/z = -0.015 (計72回の発動すべて)

**根本原因**: ケラノウス・エイドロン(0x67E1)は data_id=9020 の変身体（元はゾディアーク add）が発動するアクションで、target が null かつ target_world が (-0.015,-0.015,-0.015) という「位置なし」の placeholder 座標を持つ。

`AutoTelegraphService.OnActionUsed` の判定（`AutoTelegraphService.cs:337-343`）:
```csharp
if ((ev.TargetId is null or 0) &&
    ev.TargetWorld is { } tw &&
    MathF.Abs(tw.X) < 0.1f &&
    MathF.Abs(tw.Z) < 0.1f)
{
    _log.Debug("skip 無効座標 action..."); return;
}
```

これは `tw.X = -0.015`, `tw.Z = -0.015` をキャッチ**できない**（条件は `|x| < 0.1 && |z| < 0.1` だが -0.015 は -0.1 < -0.015 < 0 なので `Abs(-0.015) = 0.015 < 0.1` → **スキップ条件に合致する**）。

実際のゲームログでは 0x67E1 ケラノウス・エイドロンは AddObjectAoe 経路で処理されるが、data_id=9020の変身体がこのアクションを発動するため `AddObjectAoeService.OnActionUsed` ルート（行635-660）で:
- `src is IBattleNpc` → return（BattleNpc は action_used 由来の object AoE 学習対象外）

この条件（`AddObjectAoeService.cs:635-638`）:
```csharp
if (src is IBattleNpc)
{
    return;  // ← ケツァクウァトル(IBattleNpc)のアクションはここで弾かれる
}
```

しかし `LiveScan` 経路（TryFireLiveObjectSnapshot）では ObjectTable を直接走査し、出現済みのケツァクウァトル 4 体の学習済み AoE を描画する。この場合、描画は `LookupAoe()` → `ObjectAoeRuleResolver.TryResolve()` で `object_aoe_rules` の `"shape":"donut","radius_m":15` を使う。

**位置の問題（症状B本題）**: `FireGroup()` 内（`AddObjectAoeService.cs:1074-1083`）:

```csharp
var actor = _objectTable?.SearchByEntityId(member.EntityId);
var livePos = actor is not null
    ? new Vector3(actor.Position.X, actor.Position.Y, actor.Position.Z)
    : member.Pos;
```

OnAppear 時の `member.Pos` は object_appear 時点の正しい座標（89.5, 0, 89.5 など）を持っている。しかし `actor.Position` が LiveScan の瞬間に placeholder であれば、`IsUsableObjectAoePosition` チェックで弾かれる。

data_id=9020 の変身体は戦闘開始時 `position=(100,0,79)` で出現（全18録画）。これは `IsUninitializedPlaceholderPosition` が `|x-100|<0.01 && |z-100|<0.01` を見るため、`z=79` の場合はスキップされず通過する（`|79-100|=21 > 0.01`）。

**`IsUsableObjectAoePosition`（`AddObjectAoeService.cs:1179-1197`）の問題**:

```csharp
public static bool IsUsableObjectAoePosition(Vector3 worldPosition, Vector3? lockedCenter)
{
    if (MathF.Abs(worldPosition.X) < 0.001f &&
        MathF.Abs(worldPosition.Z) < 0.001f) return false;

    if (lockedCenter is { } center)
    {
        var dx = worldPosition.X - center.X;
        var dz = worldPosition.Z - center.Z;
        if (MathF.Sqrt(dx * dx + dz * dz) < PlaceholderCenterDistanceM) return false;  // 0.75m
    }
    return true;
}
```

`arena_center_x=100.714`, `arena_center_z=102.143` として計算すると、初期 placeholder の `(100, 79)` は中心から約23m離れているためこのチェックを通過し、誤った位置で AoE が描画される可能性がある。

ただし **data_id=9020 の actor は `IsRecentCastSource` チェックで suppress される可能性** があるが、変身体の actor.Name が「ケツァクウァトル」になった後は suppress キーが「ゾディアーク」のままである問題がある。

---

### 症状 C: 形状が間違う

**頻度**: トリガー定義上、確認可能な問題が 18/18 録画すべてに内在

**証拠 1: spawn_67BF_233C の形状不一致**

`月の底.json` の `predicted_object_spawns` において:

```json
// spawn_67BF_3834 (data_id=14388、本物のケツァクウァトル)
{"shape":"donut","radius_m":6,"inner_radius_m":2,...}

// spawn_67BF_233C (data_id=9020、変身体ゾディアーク add)
{"shape":"circle","radius_m":5,...}  // donut でない
```

data_id=9020 の変身体ケツァクウァトルも実際には donut アクション（0x6651）を使うが、`PredictedObjectSpawnService.FireSpawn()` は spawn 定義の shape (`circle`) をそのまま使う（`PredictedObjectSpawnService.cs:252`）。

**証拠 2: object_aoe_rules の radius_m=15 が大きすぎる問題**

`月の底.json` の `object_aoe_rules`:

```json
{"object_name":"ケツァクウァトル","shape":"donut","radius_m":15,"inner_radius_m":4.5,...}
```

実際のゲームでのケツァクウァトル AoE（action 0x6651）の Lumina 値は不明だが、予告 spawn 定義では `radius_m=6, inner_radius_m=2` となっている。

`AddObjectAoeService.FireGroup()` の oversized ガード:
```csharp
// AddObjectAoeService.cs:1061-1068
if (learned.Value.Zone.RadiusM is { } r && r > 0 &&
    arena.ArenaRadius > 0 && r >= arena.ArenaRadius * 0.9)
{
    _log.Information("[FfxivEchoes] AddObjectAoe: skip oversized...");
    continue;
}
```

`radius_m=15`, `arenaRadius=20` → `15 >= 20 * 0.9 = 18` → **スキップされない**（15 < 18）。しかし 15m ドーナツはアリーナ 20m 中に描くと極めて大きく、実際のギミック（おそらく半径 6m 以下）と大幅に異なる。

**証拠 3: action 0x6651 の名前が "Action#26193" で Lumina 名前解決失敗**

```jsonl
{"action_id":"0x6651","action_name":"Action#26193",...}
```

全18録画・全72イベントでアクション名が "Action#26193" という generic 名称。Lumina のアクションシートで 0x6651 (=26193) の name が引けていないか、Capture 時に name が未解決だった可能性。`AoeResolver.Resolve()` で行 id が取得できれば CastType と EffectRange は取得できるが、アクション名が分からない場合は `AutoSafeCallPlanner.CreateKnown` の辞書照合も失敗する。

**証拠 4: ケラノウス・エイドロン(0x67E1)の形状解決パス**

0x67E1 は data_id=9020 変身体 → `IBattleNpc` → `AddObjectAoeService.OnActionUsed` でスキップ → Lumina での形状解決がない → `recording_action` 学習がなされない。

0x67E1 の AutoTelegraph 経路:
- `target = null`, `target_x = -0.015` → `AutoTelegraphService` の 0.1m チェックで `|−0.015| = 0.015 < 0.1` でスキップ→**描画されない**

よって ケラノウス・エイドロン(0x67E1) は **どの経路でも描画されない**（18/18 録画で確認）。

---

## 3. 補足発見

### 補足 1: 戦闘開始時のゾディアーク add (data_id=9020) placeholder 問題

全18録画で、戦闘開始 t=0 時点に data_id=9020 の「ゾディアーク」 add が **20体** 出現。位置はすべて `(100, 0, 79)` という固定座標。

これは `IsUninitializedPlaceholderPosition` のチェック (`|x-100|<0.01 && |z-100|<0.01`) を **通過する**（z=79 は 100 ではない）。したがって `AddObjectAoeService.OnAppear` まで到達し、`IsUsableObjectAoePosition` でも通過する（アリーナ中心 (100.7, 102.1) から 23m 以上離れているため）。

20体もの data_id=9020 actor がアリーナ外の (100,79) に存在するため、`LookupAoe` で「ゾディアーク」のルール（enabled=false）に当たり描画はされないが、ログに大量の lookup 処理が走る。

### 補足 2: ステュクス→ケツァクウァトル出現の spawn タイミング問題

`spawn_67F3_3834`: `delay_sec=0.733`, `lead_time_sec=5` → `fireAfterSec=0`

ステュクス cast_start = t ≈ 31.5秒、ケツァクウァトル object_appear = t ≈ 32.2秒（0.72秒後）。

この spawn が `fireAfterSec=0` で即時発火するが、実際には object はすでに t=32.2秒に出現するため、cast_start（t=31.5）の時点で予告を出す意味はある。**ただし DedupWindowSec=5秒のため、パラデイグマ(0x67BF)で既に spawn_67BF_3834 が t≈25.7秒に発火している場合、ステュクスの spawn_67F3_3834 も同じ spawn.Id でないため dedup されず**、二重発火する可能性がある。ただし `pendingPredictions` は spawn.Id で管理されており、`spawn_67F3_3834 != spawn_67BF_3834` なので独立して発火する。

### 補足 3: 重複 spawn_id 問題の詳細

`月の底.json` で `"id":"spawn_67BF_233C"` が 3 エントリ:
1. trigger_event=cast_start, delay=13.413, objectName=ケツァクウァトル, data_id=9020
2. trigger_event=cast_start, delay=18.747, objectName=ゾディアーク, data_id=9020  ← 別物
3. (残りはステュクス系で id 末尾が違う可能性あり)

また `"id":"spawn_6C60_233C"` も同様に重複している（コキュートス系）。`PredictedObjectSpawnLearner` が data_id だけを変えて同一 id を生成したと推測される。

---

## 4. 修正の優先順位案

### 優先度 1（高impact・即効）: object_aoe_rules の radius 修正

**問題**: ケツァクウァトルの `radius_m=15` が実際の AoE（おそらく 6m 前後）と大幅乖離

**修正方向**: `月の底.json` の object_aoe_rules の `radius_m` を spawn 定義と一致させる（`radius_m=6, inner_radius_m=2`）か、action 0x6651 の Lumina 値で上書きする。これは設定ファイルの変更のみで完結。

### 優先度 2（高impact・コード修正）: spawn_id 重複の排除と delay_sec 精度向上

**問題**: `spawn_67BF_233C` が 3 重複 → dedup 誤動作の可能性。`delay_sec=13.411` と実測 14.9 秒の 1.5 秒差。

**修正方向**: `PredictedObjectSpawnLearner.cs` での id 生成ロジックを `cast_id + data_id + trigger_event + delay_bucket` に変更し、重複を防ぐ。delay_sec の学習は `cast_start → object_appear` の秒数（約14.9秒）を直接計測するよう修正。

### 優先度 3（中impact）: ケラノウス・エイドロン(0x67E1)の描画経路確立

**問題**: 0x67E1 が AutoTelegraph/AddObjectAoe 双方で描画されない（target_world が (-0.015,-0.015,-0.015) で位置不明のまま）

**修正方向**: 0x67E1 を `AutoSafeCallPlanner.IsRaidWide` のマークからは外しつつ、FromCaster=true として source actor の位置を使う経路を追加する。`AutoTelegraphService.OnActionUsed` の 0.1m チェックの上流に「-0.015 は placeholder」の判定を追加（`tw.X < -0.001f` などの符号チェック）し、FromCaster フォールバックで source 位置を使う。

### 優先度 4（中impact）: data_id=9020 変身体の spawn 形状統一

**問題**: `spawn_67BF_233C` の shape=circle は実際には donut が正しい

**修正方向**: `PredictedObjectSpawnLearner` での AoE 形状学習で、action 0x6651 の CastType/EffectRange を参照して shape を正しく設定する。または `spawn_67BF_233C` を手動で shape=donut に修正。

### 優先度 5（低impact・防御的）: IsUsableObjectAoePosition の placeholder 定義強化

**問題**: (100, 0, 79) が placeholder として検出されない（z=79 なので isUninitialized チェックをパス）

**修正方向**: ゾーン固有の placeholder 除外リストを設ける、または「アリーナ外かつ非常に多数の同名 actor が同一座標に存在する場合は placeholder とみなす」휴리스틱をAddObjectAoeService に追加。

---

## 5. 録画集計サマリ

| 指標 | 値 |
|---|---|
| 調査録画数 | 18 ファイル |
| パラデイグマ(0x67BF)が存在する録画数 | 17/18 |
| ケツァクウァトル(14388)の object_appear が全録画で正常な座標で来る | 17/17（89.5 or 110.5 / 89.5 or 110.5) |
| cast_start(0x67BF) → object_appear(14388) の遅延 | 14.86〜14.98秒（平均 14.91秒）|
| ケラノウス・エイドロン(0x67E1)が placeholder 座標で発動 | 17/17（4体×17=68イベント中68すべて） |
| ケツァクウァトル(0x6651 Action#26193)が self-target で正常位置 | 17/17（4体×17=68イベント中68すべて） |
| 戦闘開始時 (100,0,79) placeholder に data_id=9020 actor 20体 | 18/18 |
| `target_x=-0.015` の発生 | 18/18 録画・72イベント |

---

## 関連ファイル（絶対パス）

- `C:\Users\iiten\Documents\GitHub\ffxiv_echoes\src\FfxivEchoes\Triggers\PredictedObjectSpawnService.cs`
- `C:\Users\iiten\Documents\GitHub\ffxiv_echoes\src\FfxivEchoes\Triggers\AddObjectAoeService.cs`
- `C:\Users\iiten\Documents\GitHub\ffxiv_echoes\src\FfxivEchoes\Triggers\AutoTelegraphService.cs`
- `C:\Users\iiten\Documents\GitHub\ffxiv_echoes\src\FfxivEchoes\Triggers\AoeResolver.cs`
- `C:\Users\iiten\Documents\GitHub\ffxiv_echoes\src\FfxivEchoes\Triggers\ObjectAoeRuleResolver.cs`
- `C:\Users\iiten\Documents\GitHub\ffxiv_echoes\src\FfxivEchoes\Triggers\Models\PredictedObjectSpawn.cs`
- `C:\Users\iiten\Documents\GitHub\ffxiv_echoes\src\FfxivEchoes\Capture\ObjectCapture.cs`
- `C:\Users\iiten\AppData\Roaming\XIVLauncher\pluginConfigs\FfxivEchoes\triggers\月の底.json`
- `C:\Users\iiten\AppData\Roaming\XIVLauncher\pluginConfigs\FfxivEchoes\recordings\月の底\2026-05-15_10-05-17.jsonl`（代表録画）
