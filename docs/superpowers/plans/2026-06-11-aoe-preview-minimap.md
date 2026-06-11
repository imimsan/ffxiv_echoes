# 俯瞰図 AoE 事前描画（予測レイヤ）＋録画位置記録 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** タイムライン HUD の予測キャストについて、実効 AoE 範囲を詠唱 N 秒前からミニマップ俯瞰図に薄く事前描画し、あわせて録画 jsonl の cast_start にキャスト者の位置・向きを記録し始める。

**Architecture:** `UpcomingEventsWindow.Draw()` が毎フレーム計算する補正済み予測リスト（セグメント再アンカ・分岐棄却・フェーズ絞り込み適用済み）を新サービス `UpcomingAoePreviewService` へ渡し、形状解決（safe_call 辞書 → Lumina、cast_id 単位キャッシュ）と source actor のライブ解決（名前完全一致のみ）を経て `MinimapWindow` の新しい予測レイヤに描画する。スペック: `docs/superpowers/specs/2026-06-11-aoe-preview-minimap-design.md`。

**Tech Stack:** C# / .NET（Dalamud プラグイン）、ImGui、独自テストランナー（`src/FfxivEchoes.Tests/Program.cs`）

**前提:** ブランチ `fix/phase-transition-reset` 上で作業（ユーザー承認済み）。セグメント再アンカ作業はコミット済み（`e4b9573`）。

**ビルド・テストコマンド（全タスク共通）:**
```powershell
dotnet build src/FfxivEchoes/FfxivEchoes.csproj -p:Platform=x64   # 期待: 0 エラー（既存警告 3 件は無視）
dotnet run --project src/FfxivEchoes.Tests -p:Platform=x64        # 期待: 全行 PASS、FAIL 0
```

---

### Task 1: UpcomingItem / UpcomingTemplate に Id（cast_id）を追加し UpcomingItem を公開化

予測の cast_id がテンプレート→アイテム変換で捨てられているため、引き渡す。後続タスクの policy・サービスが `UpcomingItem` を参照できるよう、private ネスト型から public トップレベル型へ移動する。

**Files:**
- Modify: `src/FfxivEchoes/Windows/UpcomingEventsWindow.cs`

- [ ] **Step 1: UpcomingItem を public トップレベル型に移動し Id を追加**

`UpcomingEventsWindow.cs` 末尾（969-976 行）の private ネスト定義:

```csharp
    private readonly record struct UpcomingItem(
        double Time,
        string Icon,
        string Label,
        string Sub,
        string EventType,
        string? Source,
        uint Color);
```

これをクラスの**外**（ファイル末尾、`UpcomingEventsWindow` クラスの閉じ括弧の後、namespace 内）へ移動して public 化し、`Id` を追加:

```csharp
/// <summary>
/// タイムライン HUD の表示 1 行分。Id は録画予測の cast_id（"0x…" 形式、ノート/ライブ AA は null）。
/// UpcomingAoePreviewService が AoE 事前描画の形状解決キーとして使う。
/// </summary>
public readonly record struct UpcomingItem(
    double Time,
    string Icon,
    string Label,
    string Sub,
    string EventType,
    string? Source,
    uint Color,
    string? Id = null);
```

- [ ] **Step 2: UpcomingTemplate に Id を追加**

978-986 行の `UpcomingTemplate`（こちらは private ネストのままで良い）:

```csharp
    private readonly record struct UpcomingTemplate(
        double RelativeTime,
        string Icon,
        string Label,
        string Sub,
        string EventType,
        string? Source,
        uint Color,
        bool ApplySyncOffset = true,
        string? Id = null);
```

- [ ] **Step 3: BuildUpcomingTemplates で prediction.Id を引き渡す**

886-894 行（録画予測のテンプレート生成）に `Id` を追加:

```csharp
                list.Add(new UpcomingTemplate(
                    RelativeTime: prediction.RelativeSeconds,
                    Icon: isAa ? "AA" : GuessIconForCast(label),
                    Label: label,
                    Sub: FormatPredictionSub(prediction),
                    EventType: prediction.EventType,
                    Source: prediction.Source,
                    Color: color,
                    ApplySyncOffset: !useSegmentMode,
                    Id: isAa ? null : prediction.Id));
```

ノート生成（906-916 行）とライブ AA（684-692 行）は `Id` を渡さない（デフォルト null のまま）。

- [ ] **Step 4: CollectUpcoming で Id をアイテムへ伝搬**

609-616 行のテンプレート→アイテム変換:

```csharp
            list.Add(new UpcomingItem(
                Time: t,
                Icon: template.Icon,
                Label: template.Label,
                Sub: template.Sub,
                EventType: template.EventType,
                Source: template.Source,
                Color: template.Color,
                Id: template.Id));
```

642-649 行（ライブ AA の変換）は `Id` を渡さない（null のまま）。

- [ ] **Step 5: ビルドと既存テストの確認**

```powershell
dotnet build src/FfxivEchoes/FfxivEchoes.csproj -p:Platform=x64
dotnet run --project src/FfxivEchoes.Tests -p:Platform=x64
```
期待: ビルド 0 エラー、テスト FAIL 0（挙動変更なし）。

- [ ] **Step 6: コミット**

```powershell
git add src/FfxivEchoes/Windows/UpcomingEventsWindow.cs
git commit -m "refactor(timeline): UpcomingItem に cast_id を保持し公開型へ（AoE事前描画の下準備）"
```

---

### Task 2: PredictedAoePreviewPolicy（純粋ロジック）を TDD で追加

候補選択・超大型スキップ・確定キャスト抑制の純粋関数。Dalamud 型に依存しない（テスト制約: `test_dalamud_type_constraint`）。

**Files:**
- Create: `src/FfxivEchoes/Windows/PredictedAoePreviewPolicy.cs`
- Test: `src/FfxivEchoes.Tests/Program.cs`

- [ ] **Step 1: 失敗するテストを書く**

`Program.cs` のテスト登録リスト（17-239 行の `var tests = new List<(string Name, Action Body)>`）末尾に追加:

```csharp
    ("PredictedAoePreview selects cast_start candidates within window", PredictedAoePreview_SelectsCandidatesWithinWindow),
    ("PredictedAoePreview dedups same cast and caps count", PredictedAoePreview_DedupsSameCastAndCapsCount),
    ("PredictedAoePreview skips oversized AoE", PredictedAoePreview_SkipsOversizedAoe),
    ("PredictedAoePreview suppressed by confirmed cast", PredictedAoePreview_SuppressedByConfirmedCast),
```

テスト本体（既存の static メソッド群の並びに追加。`using FfxivEchoes.Windows;` が無ければファイル先頭に追加）:

```csharp
static void PredictedAoePreview_SelectsCandidatesWithinWindow()
{
    var items = new List<UpcomingItem>
    {
        new(Time: 99.0, Icon: "⚡", Label: "過去技", Sub: "", EventType: "cast_start", Source: "ケフカ", Color: 0u, Id: "0x9E11"),
        new(Time: 102.0, Icon: "⚡", Label: "両翼斬り", Sub: "", EventType: "cast_start", Source: "ケフカ", Color: 0u, Id: "0x9E00"),
        new(Time: 103.0, Icon: "AA", Label: "AA", Sub: "", EventType: "auto_attack", Source: "ケフカ", Color: 0u),
        new(Time: 104.0, Icon: "⚡", Label: "両翼斬り", Sub: "", EventType: "cast_start", Source: "ケフカ", Color: 0u, Id: "0x9E01"),
        new(Time: 105.0, Icon: "東", Label: "散開", Sub: "note", EventType: "note", Source: null, Color: 0u),
        new(Time: 130.0, Icon: "⚡", Label: "ウェイブ", Sub: "", EventType: "cast_start", Source: "ケフカ", Color: 0u, Id: "0x9E10"),
    };
    var output = new List<UpcomingItem>();
    PredictedAoePreviewPolicy.SelectPreviewCandidates(items, nowRel: 100.0, advanceSec: 10.0, maxItems: 4, output);

    Equal(2, output.Count, "窓内の cast_start のみ（過去・窓外・AA・note は除外）");
    Equal("0x9E00", output[0].Id, "時刻順 1 件目");
    Equal("0x9E01", output[1].Id, "同名でも別 cast_id（真偽の両候補）は両方残る");
}

static void PredictedAoePreview_DedupsSameCastAndCapsCount()
{
    var items = new List<UpcomingItem>
    {
        new(Time: 101.0, Icon: "⚡", Label: "技A", Sub: "", EventType: "cast_start", Source: "ケフカ", Color: 0u, Id: "0x9E00"),
        new(Time: 102.0, Icon: "⚡", Label: "技A", Sub: "", EventType: "cast_start", Source: "ケフカ", Color: 0u, Id: "0x9E00"),
        new(Time: 103.0, Icon: "⚡", Label: "技B", Sub: "", EventType: "cast_start", Source: "ケフカ", Color: 0u, Id: "0x9E01"),
        new(Time: 104.0, Icon: "⚡", Label: "技C", Sub: "", EventType: "cast_start", Source: "ケフカ", Color: 0u, Id: "0x9E02"),
        new(Time: 105.0, Icon: "⚡", Label: "技D", Sub: "", EventType: "cast_start", Source: "ケフカ", Color: 0u, Id: "0x9E03"),
    };
    var output = new List<UpcomingItem>();
    PredictedAoePreviewPolicy.SelectPreviewCandidates(items, nowRel: 100.0, advanceSec: 10.0, maxItems: 3, output);

    Equal(3, output.Count, "同一 cast_id+source は 1 件に集約され、maxItems で打ち切る");
    Equal("0x9E00", output[0].Id, "重複は最早 1 件");
    Equal("0x9E01", output[1].Id, "2 件目");
    Equal("0x9E02", output[2].Id, "3 件目（0x9E03 は cap で落ちる）");
}

static void PredictedAoePreview_SkipsOversizedAoe()
{
    True(PredictedAoePreviewPolicy.ShouldSkipOversized(2, 25f), "円形 25m は全体扱い");
    True(PredictedAoePreviewPolicy.ShouldSkipOversized(5, 30f), "PBAoE 30m は全体扱い");
    False(PredictedAoePreviewPolicy.ShouldSkipOversized(2, 24.9f), "円形 24.9m は表示");
    True(PredictedAoePreviewPolicy.ShouldSkipOversized(3, 30f), "扇 30m は全体扱い");
    False(PredictedAoePreviewPolicy.ShouldSkipOversized(3, 29.9f), "扇 29.9m は表示");
}

static void PredictedAoePreview_SuppressedByConfirmedCast()
{
    True(PredictedAoePreviewPolicy.IsSuppressedByConfirmedCast(105.0, 103.0), "確定 ±6s 内は抑制");
    False(PredictedAoePreviewPolicy.IsSuppressedByConfirmedCast(115.0, 103.0), "12s 離れた次回出現は抑制しない");
    False(PredictedAoePreviewPolicy.IsSuppressedByConfirmedCast(105.0, null), "未観測なら抑制しない");
}
```

- [ ] **Step 2: テストが失敗（コンパイルエラー）することを確認**

```powershell
dotnet build src/FfxivEchoes.Tests -p:Platform=x64
```
期待: `PredictedAoePreviewPolicy` が存在せず CS0103 でビルド失敗。

- [ ] **Step 3: PredictedAoePreviewPolicy を実装**

`src/FfxivEchoes/Windows/PredictedAoePreviewPolicy.cs` を新規作成:

```csharp
using System;
using System.Collections.Generic;

namespace FfxivEchoes.Windows;

/// <summary>
/// 俯瞰図への AoE 事前描画（予測レイヤ）の純粋ロジック。
/// Dalamud 型に依存しない（テスト可能）。SPEC: docs/superpowers/specs/2026-06-11-aoe-preview-minimap-design.md
/// </summary>
public static class PredictedAoePreviewPolicy
{
    /// <summary>同時に事前描画する最大件数。視覚ノイズとフレームコストの上限。</summary>
    public const int MaxPreviewItems = 4;

    /// <summary>実 cast_start 観測後、この秒数以内の同 cast_id 予測は確定描画へ譲る。</summary>
    public const double ConfirmSuppressWindowSec = 6.0;

    /// <summary>
    /// 補正済み upcoming リスト（時刻昇順前提）から事前描画候補を選ぶ。
    /// cast_start かつ Id あり、残り 0 &lt; t ≤ advanceSec のものを最大 maxItems 件。
    /// 同一 cast_id+source は最早 1 件に集約（同名別 cast_id ＝真偽の両候補は両方残す）。
    /// </summary>
    public static void SelectPreviewCandidates(
        IReadOnlyList<UpcomingItem> items,
        double nowRel,
        double advanceSec,
        int maxItems,
        List<UpcomingItem> output)
    {
        output.Clear();
        if (advanceSec <= 0 || maxItems <= 0)
        {
            return;
        }

        foreach (var item in items)
        {
            if (output.Count >= maxItems)
            {
                break;
            }
            if (!string.Equals(item.EventType, "cast_start", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (string.IsNullOrEmpty(item.Id))
            {
                continue;
            }
            var remaining = item.Time - nowRel;
            if (remaining <= 0 || remaining > advanceSec)
            {
                continue;
            }
            if (ContainsSameCast(output, item))
            {
                continue;
            }
            output.Add(item);
        }
    }

    private static bool ContainsSameCast(List<UpcomingItem> selected, UpcomingItem item)
    {
        foreach (var s in selected)
        {
            if (string.Equals(s.Id, item.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.Source ?? string.Empty, item.Source ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 円形（CastType 2/5）25m 以上・その他 30m 以上は回避不能の全体攻撃扱いで事前描画しない
    /// （旧 PredictedCastReminderService.TryDrawPredictedMarker と同じ基準）。
    /// </summary>
    public static bool ShouldSkipOversized(int castType, float radiusM)
        => (castType is 2 or 5 && radiusM >= 25f) || radiusM >= 30f;

    /// <summary>
    /// 実 cast_start を観測済み（confirmedAtRel）の予測は ±windowSec の間、事前描画を止めて
    /// 既存の確定描画（AutoTelegraphService）に譲る。次回出現（窓外）は再び事前描画する。
    /// </summary>
    public static bool IsSuppressedByConfirmedCast(
        double itemTimeRel,
        double? confirmedAtRel,
        double windowSec = ConfirmSuppressWindowSec)
        => confirmedAtRel is { } c && Math.Abs(itemTimeRel - c) <= windowSec;
}
```

- [ ] **Step 4: テストが通ることを確認**

```powershell
dotnet run --project src/FfxivEchoes.Tests -p:Platform=x64
```
期待: 新規 4 テスト含め全行 PASS、FAIL 0。

- [ ] **Step 5: コミット**

```powershell
git add src/FfxivEchoes/Windows/PredictedAoePreviewPolicy.cs src/FfxivEchoes.Tests/Program.cs
git commit -m "feat(minimap): AoE事前描画の候補選択・抑制ポリシーを純粋関数で追加"
```

---

### Task 3: Configuration と設定 UI

**Files:**
- Modify: `src/FfxivEchoes/Configuration.cs`
- Modify: `src/FfxivEchoes/Windows/Tabs/GeneralSettingsTab.cs`

- [ ] **Step 1: Configuration にプロパティを追加**

`Configuration.cs` の「取り込み・ログ保管」プロパティ群の後に追加:

```csharp
    // 俯瞰図（ミニマップ）への予測 AoE 事前描画
    public bool ShowPredictedAoeOnMinimap { get; set; } = true;
    public double PredictedAoeAdvanceSec { get; set; } = 10.0;
```

`ClampToValidRanges()` の本体に追加:

```csharp
        PredictedAoeAdvanceSec = Math.Clamp(PredictedAoeAdvanceSec, 1.0, 30.0);
```

（ファイル先頭に `using System;` が無ければ追加。既存の Clamp 行の書式に合わせる。）

- [ ] **Step 2: GeneralSettingsTab に UI を追加**

`GeneralSettingsTab.cs` の既存項目（DebugMode チェックボックス等）の並びに、既存のレイアウトパターン（`SameLine(180f * ImGuiHelpers.GlobalScale)` / `IsItemDeactivatedAfterEdit()`）に合わせて追加:

```csharp
        ImGui.Separator();
        ImGui.TextUnformatted("俯瞰図（ミニマップ）");

        var showPredictedAoe = _configuration.ShowPredictedAoeOnMinimap;
        if (ImGui.Checkbox("予測 AoE を事前表示する", ref showPredictedAoe))
        {
            _configuration.ShowPredictedAoeOnMinimap = showPredictedAoe;
            _configuration.Save();
        }
        ImGui.TextDisabled("タイムライン予測の技範囲を、詠唱開始前から俯瞰図に薄く表示します。");

        var advance = (float)_configuration.PredictedAoeAdvanceSec;
        ImGui.AlignTextToFramePadding();
        ImGui.Text("事前表示の秒数");
        ImGui.SameLine(180f * ImGuiHelpers.GlobalScale);
        ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderFloat("##predicted-aoe-advance", ref advance, 1f, 30f, "%.0f s"))
        {
            _configuration.PredictedAoeAdvanceSec = Math.Clamp(advance, 1f, 30f);
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            _configuration.Save();
        }
```

- [ ] **Step 3: ビルド確認とコミット**

```powershell
dotnet build src/FfxivEchoes/FfxivEchoes.csproj -p:Platform=x64
git add src/FfxivEchoes/Configuration.cs src/FfxivEchoes/Windows/Tabs/GeneralSettingsTab.cs
git commit -m "feat(config): 予測AoE事前表示の設定（ON/OFF・事前秒数）を追加"
```

---

### Task 4: MinimapWindow に予測レイヤを追加

**Files:**
- Modify: `src/FfxivEchoes/Windows/MinimapWindow.cs`

- [ ] **Step 1: PredictedAoePreviewItem 型を追加**

`MinimapWindow.cs` の namespace 直下（`MinimapWindow` クラス定義の前か後、トップレベル）に public 型を追加:

```csharp
/// <summary>
/// 俯瞰図に事前描画する予測 AoE の 1 件分。UpcomingAoePreviewService が毎フレーム更新する
/// （位置・向き・残り秒はサービス側でライブ解決済み）。
/// </summary>
public readonly record struct PredictedAoePreviewItem(
    string Label,
    double RemainingSec,
    string Gimmick,
    double FanDeg,
    float? DirectionAngleRad,
    Vector3 SourceWorld,
    float? AoeRadius,
    float? AoeHalfWidthM,
    int? AoeCastType,
    uint? AoeOmenId,
    float ArenaRadius,
    string ArenaShape,
    float ArenaHalfWidth,
    float ArenaHalfDepth,
    Vector3? LockedArenaCenter);
```

- [ ] **Step 2: フィールドと SetPredictedAoePreview API を追加**

`_items` フィールド（59 行付近）の隣に:

```csharp
    // 予測 AoE 事前描画レイヤ。UpcomingAoePreviewService が毎フレーム差し替える。
    // タイムライン HUD 非表示等で更新が止まったら PredictedPreviewStaleAfter で自動クリア（残留防止）。
    private readonly List<PredictedAoePreviewItem> _predictedPreview = new();
    private DateTimeOffset _predictedPreviewUpdatedAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan PredictedPreviewStaleAfter = TimeSpan.FromSeconds(1.5);
```

`AddArenaView` の後に public メソッドを追加:

```csharp
    /// <summary>
    /// 予測 AoE レイヤを丸ごと差し替える（毎フレーム呼ばれる前提。リストはコピーされる）。
    /// 空リストでクリア。
    /// </summary>
    public void SetPredictedAoePreview(IReadOnlyList<PredictedAoePreviewItem> items)
    {
        lock (_gate)
        {
            _predictedPreview.Clear();
            for (var i = 0; i < items.Count; i++)
            {
                _predictedPreview.Add(items[i]);
            }
            _predictedPreviewUpdatedAt = DateTimeOffset.UtcNow;
        }
        if (items.Count > 0)
        {
            IsOpen = true;
        }
    }
```

- [ ] **Step 3: DrawGimmickBody / DrawActualAoeShape に色オーバーライドを追加**

`DrawActualAoeShape`（905 行）のシグネチャを拡張し、内部の fill/stroke 導出（940-941 行付近）を差し替え:

```csharp
    private void DrawActualAoeShape(
        ImDrawListPtr draw, Vector2 mapCenter, float mapR, ArenaItem item,
        uint? fillOverride = null, uint? strokeOverride = null)
```

```csharp
        var fill = fillOverride ?? ((ColDanger & 0x00FFFFFFu) | 0x55000000u);
        var stroke = strokeOverride ?? ((ColDangerLine & 0x00FFFFFFu) | 0xFF000000u);
```

`DrawGimmickBody` も同様にシグネチャへ `uint? fillOverride = null, uint? strokeOverride = null` を追加し、メソッド先頭で

```csharp
        var fillCol = fillOverride ?? ColDanger;
        var lineCol = strokeOverride ?? ColDangerLine;
```

を定義して、**このメソッド本体内**の `ColDanger` 使用箇所を `fillCol`、`ColDangerLine` 使用箇所を `lineCol` に機械的に置換する（他メソッドは触らない）。既存呼び出し（276-278 行）は引数省略でそのまま＝挙動不変。

- [ ] **Step 4: DrawCore に予測レイヤを統合**

`DrawCore()`（233-310 行）を次のように変更する。

(a) lock ブロック（240-250 行）内に stale クリアとスナップショットを追加:

```csharp
        ArenaDisplayGroup[] activeGroups;
        PredictedAoePreviewItem[] preview;
        lock (_gate)
        {
            _items.RemoveAll(it => it.ExpiresAt <= now);
            if (_predictedPreview.Count > 0 && now - _predictedPreviewUpdatedAt > PredictedPreviewStaleAfter)
            {
                _predictedPreview.Clear();
            }
            var activeSorted = _items
                .OrderByDescending(it => it.Priority)
                .ThenBy(it => it.ExpiresAt)
                .ToArray();
            activeGroups = BuildDisplayGroups(activeSorted)
                .Take(3)
                .ToArray();
            preview = _predictedPreview.Count == 0
                ? Array.Empty<PredictedAoePreviewItem>()
                : _predictedPreview.ToArray();
        }
```

(b) 空判定（252-256 行）を両方空のときだけ閉じるよう変更:

```csharp
        if (activeGroups.Length == 0 && preview.Length == 0)
        {
            IsOpen = false;
            return;
        }
```

(c) `var draw = ...; var scale = ...;` の直後に「予測のみ」の描画パスを追加:

```csharp
        if (activeGroups.Length == 0)
        {
            // 確定 AoE が無く予測のみ：1 枚目相当のタイルにアリーナ + 予測形状 + ライブ位置を描く
            var tilePos = ImGui.GetCursorScreenPos();
            var size = ArenaSize * scale;
            var center = new Vector2(tilePos.X + size * 0.5f, tilePos.Y + size * 0.5f);
            var r = size * 0.5f - 4f * scale;
            var baseItem = BuildPreviewArenaItem(preview[0]);
            DrawArena(draw, center, r, baseItem);
            DrawPredictedPreview(draw, center, r, scale, preview);
            DrawBoss(draw, center, r, scale, baseItem);
            DrawPlayerPositions(draw, center, r, scale, baseItem, drawLiveContext: true);
            ImGui.Dummy(new Vector2(size, size));
            DrawCallout(draw, tilePos, size, scale,
                $"予測: {preview[0].Label}",
                $"{Math.Max(0, preview[0].RemainingSec):0.0}s");
            ImGui.Dummy(new Vector2(size, CalloutHeight * scale));
            ImGui.Spacing();
            return;
        }
```

(d) 既存のグループ描画ループ内、`foreach (var layer in group.Items)` ループの**直後**（`DrawSafeZoneOverlay` の前）に追加（1 枚目のフルサイズタイルにのみ重畳。安置・ボス・PT のライブ情報は予測より手前に残る）:

```csharp
            if (idx == 0 && preview.Length > 0)
            {
                DrawPredictedPreview(draw, center, r, scale * tileScale, preview);
            }
```

- [ ] **Step 5: 描画ヘルパ 2 つを追加**

`DrawCore` の後（`BuildDisplayGroups` の前あたり）に追加:

```csharp
    // 予測レイヤの配色：確定（赤系）と明確に区別するアンバー。fill α≈0x30、stroke α≈0xA0。
    private const uint PreviewFill = 0x3024BFFFu;
    private const uint PreviewStroke = 0xA024BFFFu;
    private const uint PreviewText = 0xFF24BFFFu;

    private void DrawPredictedPreview(
        ImDrawListPtr draw, Vector2 center, float r, float scale,
        PredictedAoePreviewItem[] preview)
    {
        foreach (var p in preview)
        {
            var item = BuildPreviewArenaItem(p);
            DrawGimmickBody(draw, center, r, item, PreviewFill, PreviewStroke);
            DrawActualAoeShape(draw, center, r, item, PreviewFill, PreviewStroke);

            var labelPos = TryProjectWorldToMap(center, r, item, p.SourceWorld, out var sp) ? sp : center;
            var text = $"{p.Label} {Math.Max(0, p.RemainingSec):0}s";
            var ts = ImGui.CalcTextSize(text);
            var anchor = new Vector2(labelPos.X - ts.X * 0.5f, labelPos.Y - ts.Y - 6f * scale);
            draw.AddText(new Vector2(anchor.X + 1f, anchor.Y + 1f), 0xCC000000u, text);
            draw.AddText(anchor, PreviewText, text);
        }
    }

    private static ArenaItem BuildPreviewArenaItem(PredictedAoePreviewItem p) => new(
        Gimmick: p.Gimmick,
        Callout: p.Label,
        Priority: 0,
        Direction: null,
        FanDeg: p.FanDeg,
        ArenaRadius: p.ArenaRadius,
        SafeZoneWorld: null,
        SafeZoneRadius: 3f,
        DirectionAngleRad: p.DirectionAngleRad,
        SourceWorld: p.SourceWorld,
        StrategyPositions: Array.Empty<StrategyPosition>(),
        AoeRadius: p.AoeRadius,
        AoeHalfWidthM: p.AoeHalfWidthM,
        AoeCastType: p.AoeCastType,
        AoeOmenId: p.AoeOmenId,
        MultiSourceWorlds: Array.Empty<Vector3>(),
        ObjectMarkers: Array.Empty<StrategyObjectMarker>(),
        AoeZones: Array.Empty<StrategyAoeZone>(),
        PartyStatusHighlights: Array.Empty<StatusHighlightSpec>(),
        ArenaShape: p.ArenaShape,
        ArenaHalfWidth: p.ArenaHalfWidth,
        ArenaHalfDepth: p.ArenaHalfDepth,
        LockedArenaCenter: p.LockedArenaCenter,
        ExpiresAt: DateTimeOffset.MaxValue,
        AutoLuminaCastId: null);
```

注意: `ArenaItem` のコンストラクタ引数名・並びは実ファイル（1657-1687 行）に合わせて検証すること。`DrawCallout` / `DrawArena` / `DrawBoss` / `DrawPlayerPositions` のシグネチャが上記と異なる場合は実ファイルに合わせる。

- [ ] **Step 6: ビルド確認とコミット**

```powershell
dotnet build src/FfxivEchoes/FfxivEchoes.csproj -p:Platform=x64
dotnet run --project src/FfxivEchoes.Tests -p:Platform=x64
git add src/FfxivEchoes/Windows/MinimapWindow.cs
git commit -m "feat(minimap): 予測AoE事前描画レイヤ（アンバー薄表示・残り秒ラベル・stale自動クリア）"
```

---

### Task 5: UpcomingAoePreviewService とサービス配線

**Files:**
- Create: `src/FfxivEchoes/Triggers/UpcomingAoePreviewService.cs`
- Modify: `src/FfxivEchoes/Windows/UpcomingEventsWindow.cs`（Publish 接続）
- Modify: `src/FfxivEchoes/Plugin.cs`（生成・Dispose）

- [ ] **Step 1: サービス本体を作成**

`src/FfxivEchoes/Triggers/UpcomingAoePreviewService.cs` を新規作成:

```csharp
using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FfxivEchoes.Capture;
using FfxivEchoes.Diagnostics;
using FfxivEchoes.Events;
using FfxivEchoes.Triggers.Models;
using FfxivEchoes.Windows;

namespace FfxivEchoes.Triggers;

/// <summary>
/// タイムライン HUD（UpcomingEventsWindow）の補正済み予測リストを入力に、残り
/// PredictedAoeAdvanceSec 秒以内の cast_start 予測の実効 AoE 範囲をミニマップ俯瞰図へ
/// 事前描画する。形状解決は safe_call 辞書（真偽等の実効範囲）→ Lumina の既存パスを
/// cast_id 単位でキャッシュ。source actor は名前完全一致のみで解決し、解決できなければ
/// 描画しない（旧予測描画の回帰原因だった最大 HP fallback は行わない）。
/// </summary>
public sealed class UpcomingAoePreviewService : IDisposable
{
    private static readonly TimeSpan TableRescanInterval = TimeSpan.FromSeconds(0.5);

    private readonly TriggerStore _store;
    private readonly CombatClock _combatClock;
    private readonly IDataManager _dataManager;
    private readonly IObjectTable _objectTable;
    private readonly MinimapWindow _minimap;
    private readonly Configuration _config;
    private readonly IPluginLog _log;

    private readonly IDisposable _eventSub;
    private readonly object _gate = new();
    private readonly Dictionary<uint, PreviewGeometry?> _geometryCache = new();
    private readonly Dictionary<string, uint> _sourceIdByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, double> _lastConfirmRel = new();
    private readonly List<UpcomingItem> _candidateBuffer = new();
    private readonly List<PredictedAoePreviewItem> _itemBuffer = new();
    private string _currentZone = "Unknown";
    private DateTimeOffset _lastTableScan = DateTimeOffset.MinValue;
    private volatile bool _cachesDirty;
    private bool _lastPublishHadItems;

    public UpcomingAoePreviewService(
        IEventBus bus,
        TriggerStore store,
        CombatClock combatClock,
        IDataManager dataManager,
        IObjectTable objectTable,
        MinimapWindow minimap,
        Configuration config,
        IPluginLog log)
    {
        _store = store;
        _combatClock = combatClock;
        _dataManager = dataManager;
        _objectTable = objectTable;
        _minimap = minimap;
        _config = config;
        _log = log;
        _eventSub = bus.SubscribeAll(OnEvent);
        // Reloaded はワーカースレッド発火（reloaded_thread 既知制約）。フラグだけ立てて
        // 実処理は次の Publish（メインスレッド）で行う。
        _store.Reloaded += OnTriggerStoreReloaded;
    }

    public void Dispose()
    {
        _eventSub.Dispose();
        _store.Reloaded -= OnTriggerStoreReloaded;
    }

    private void OnTriggerStoreReloaded() => _cachesDirty = true;

    private void OnEvent(IGameEvent ev)
    {
        switch (ev)
        {
            case ZoneChangedEvent z:
                _currentZone = string.IsNullOrEmpty(z.ZoneName) ? "Unknown" : z.ZoneName;
                _cachesDirty = true;
                lock (_gate)
                {
                    _lastConfirmRel.Clear();
                }
                break;
            case CombatStartedEvent:
            case CombatEndedEvent:
                lock (_gate)
                {
                    _lastConfirmRel.Clear();
                }
                break;
            case CastStartedEvent c:
                var rel = _combatClock.RelativeSecondsAt(c.Timestamp);
                if (rel is not null)
                {
                    lock (_gate)
                    {
                        _lastConfirmRel[c.CastActionId] = rel.Value;
                    }
                }
                break;
        }
    }

    /// <summary>
    /// UpcomingEventsWindow.Draw() から毎フレーム呼ばれる（メインスレッド）。
    /// items は CollectUpcoming の出力（時刻昇順・補正済み・表示用 dedup 前）。
    /// </summary>
    public void Publish(IReadOnlyList<UpcomingItem> items, double nowRel)
    {
        try
        {
            PublishCore(items, nowRel);
        }
        catch (Exception ex)
        {
            FrameErrorThrottle.Report(_log, ex, "UpcomingAoePreviewService.Publish");
        }
    }

    private void PublishCore(IReadOnlyList<UpcomingItem> items, double nowRel)
    {
        if (!_config.ShowPredictedAoeOnMinimap)
        {
            ClearIfNeeded();
            return;
        }
        if (_cachesDirty)
        {
            _geometryCache.Clear();
            _sourceIdByName.Clear();
            _cachesDirty = false;
        }

        PredictedAoePreviewPolicy.SelectPreviewCandidates(
            items, nowRel, _config.PredictedAoeAdvanceSec,
            PredictedAoePreviewPolicy.MaxPreviewItems, _candidateBuffer);

        var file = _store.GetByZone(_currentZone);
        var arena = AutoAoeDisplayPolicy.ResolveArena(file);

        _itemBuffer.Clear();
        foreach (var c in _candidateBuffer)
        {
            if (!AoeResolver.TryParseCastId(c.Id ?? string.Empty, out var actionId) || actionId == 0)
            {
                continue;
            }

            double? confirmed;
            lock (_gate)
            {
                confirmed = _lastConfirmRel.TryGetValue(actionId, out var t) ? t : (double?)null;
            }
            if (PredictedAoePreviewPolicy.IsSuppressedByConfirmedCast(c.Time, confirmed))
            {
                continue;
            }

            var geom = ResolveGeometry(actionId, c.Label, file);
            if (geom is null)
            {
                continue;
            }

            var npc = ResolveSourceStrict(c.Source);
            if (npc is null)
            {
                continue;
            }

            var radius = geom.Aoe is null
                ? (float?)null
                : AoeResolver.EffectiveRadius(geom.Aoe, npc.HitboxRadius);
            var facing = geom.UsesFacing
                ? ArenaProjection.RotationToMapAngleRad(npc.Rotation)
                : (float?)null;

            _itemBuffer.Add(new PredictedAoePreviewItem(
                Label: c.Label,
                RemainingSec: c.Time - nowRel,
                Gimmick: geom.Gimmick,
                FanDeg: geom.FanDeg ?? 90.0,
                DirectionAngleRad: facing,
                SourceWorld: npc.Position,
                AoeRadius: radius,
                AoeHalfWidthM: geom.Aoe is { HalfWidthM: > 0f } a ? a.HalfWidthM : null,
                AoeCastType: geom.Aoe?.CastType,
                AoeOmenId: geom.Aoe?.OmenId,
                ArenaRadius: (float)arena.ArenaRadius,
                ArenaShape: arena.ArenaShape,
                ArenaHalfWidth: arena.ArenaWidth is { } w && w > 0 ? (float)(w * 0.5) : (float)arena.ArenaRadius,
                ArenaHalfDepth: arena.ArenaDepth is { } d && d > 0 ? (float)(d * 0.5) : (float)arena.ArenaRadius,
                LockedArenaCenter: arena.LockedArenaCenter));
        }

        if (_itemBuffer.Count > 0 || _lastPublishHadItems)
        {
            _minimap.SetPredictedAoePreview(_itemBuffer);
        }
        _lastPublishHadItems = _itemBuffer.Count > 0;
    }

    private void ClearIfNeeded()
    {
        if (!_lastPublishHadItems)
        {
            return;
        }
        _itemBuffer.Clear();
        _minimap.SetPredictedAoePreview(_itemBuffer);
        _lastPublishHadItems = false;
    }

    private PreviewGeometry? ResolveGeometry(uint actionId, string label, TriggerFile? file)
    {
        if (_geometryCache.TryGetValue(actionId, out var cached))
        {
            return cached;
        }
        var resolved = ResolveGeometryCore(actionId, label, file);
        _geometryCache[actionId] = resolved;
        return resolved;
    }

    private PreviewGeometry? ResolveGeometryCore(uint actionId, string label, TriggerFile? file)
    {
        if (AutoSafeCallPlanner.IsRaidWide(file, actionId, label))
        {
            return null;
        }
        var match = new MatchCondition { CastId = $"0x{actionId:X}", CastName = label };
        if (AutoSafeCallPlanner.ShouldSuppressMinimap(file, match))
        {
            return null;
        }

        var known = AutoSafeCallPlanner.CreateKnown(actionId, label);
        var aoe = AoeResolver.Resolve(_dataManager, actionId, _log);
        if (aoe is not null && PredictedAoePreviewPolicy.ShouldSkipOversized(aoe.CastType, aoe.Radius))
        {
            if (known is null)
            {
                return null;
            }
            // 辞書で実効形状が明示されている（真偽の two_side_cleave 等）場合は、Lumina 上の
            // 巨大 EffectRange を捨ててギミック形状のみ描く（アリーナ半径基準のデフォルト寸法）。
            aoe = null;
        }

        var safeCall = known ?? (aoe is not null ? AutoSafeCallPlanner.Create(aoe, label) : null);
        var visual = safeCall ?? (aoe is not null ? AutoSafeCallPlanner.CreateVisual(aoe, label) : null);
        if (visual is null)
        {
            return null;
        }

        return new PreviewGeometry(
            Gimmick: visual.Gimmick,
            FanDeg: visual.FanDeg,
            Aoe: aoe,
            UsesFacing: ArenaProjection.UsesFacing(visual.Gimmick));
    }

    private IBattleNpc? ResolveSourceStrict(string? sourceName)
    {
        if (string.IsNullOrEmpty(sourceName))
        {
            return null;
        }

        if (_sourceIdByName.TryGetValue(sourceName, out var id))
        {
            if (_objectTable.SearchById(id) is IBattleNpc cachedNpc &&
                string.Equals(cachedNpc.Name.TextValue, sourceName, StringComparison.OrdinalIgnoreCase))
            {
                return cachedNpc;
            }
            _sourceIdByName.Remove(sourceName);
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastTableScan < TableRescanInterval)
        {
            return null;
        }
        _lastTableScan = now;

        foreach (var obj in _objectTable)
        {
            if (obj is not IBattleNpc npc)
            {
                continue;
            }
            if (npc.MaxHp == 0)
            {
                continue;
            }
            if (string.Equals(npc.Name.TextValue, sourceName, StringComparison.OrdinalIgnoreCase))
            {
                _sourceIdByName[sourceName] = npc.EntityId;
                return npc;
            }
        }

        // 名前完全一致のみ。最大 HP fallback は「中央の謎ドーナツ」回帰の根本原因なので行わない。
        return null;
    }

    private sealed record PreviewGeometry(
        string Gimmick,
        double? FanDeg,
        AoeResolver.AoeInfo? Aoe,
        bool UsesFacing);
}
```

注意: `AutoSafeCallPlanner.RadiusOverride` による辞書半径（`aoe_radius_m`）は `KnownAoeGeometry` 経由でなくギミック描画のデフォルト寸法に任せる設計（確定描画と同じ見え方になる）。コンパイルエラーが出た型名（`MatchCondition` の namespace 等）は実ファイルに合わせて修正する。

- [ ] **Step 2: UpcomingEventsWindow に Publish 接続**

`UpcomingEventsWindow` にプロパティを追加（フィールド群の近く）:

```csharp
    /// <summary>俯瞰図への AoE 事前描画ブリッジ。Plugin 起動時に注入される（null なら無効）。</summary>
    public UpcomingAoePreviewService? AoePreview { get; set; }
```

`Draw()`（170-181 行）の `CollectUpcoming` 直後に 1 行追加（表示用 dedup 前のリストを渡す＝真偽両候補が届く）:

```csharp
        var items = CollectUpcoming(nowRel.Value);
        AoePreview?.Publish(items, nowRel.Value);
        DrawHeroAndList(nowRel.Value, items);
```

- [ ] **Step 3: Plugin.cs で生成・注入・Dispose**

フィールド宣言（`_predictedCastReminder` の宣言の近く、同じ書式で）:

```csharp
    private readonly UpcomingAoePreviewService _upcomingAoePreview;
```

生成（PredictedCastReminderService 生成ブロック＝365-371 行の直後。`_minimapWindow`・`_upcomingWindow` は生成済みの位置であること）:

```csharp
        _upcomingAoePreview = new UpcomingAoePreviewService(
            _eventBus, _triggerStore, _combatClock, DataManager, ObjectTable,
            _minimapWindow, Configuration, Log);
        _upcomingWindow.AoePreview = _upcomingAoePreview;
```

Dispose（`SafeDispose(_predictedCastReminder, ...)` の直後）:

```csharp
        SafeDispose(_upcomingAoePreview, nameof(_upcomingAoePreview));
```

- [ ] **Step 4: ビルド・全テスト確認**

```powershell
dotnet build src/FfxivEchoes/FfxivEchoes.csproj -p:Platform=x64
dotnet run --project src/FfxivEchoes.Tests -p:Platform=x64
```
期待: 0 エラー、FAIL 0。

- [ ] **Step 5: コミット**

```powershell
git add src/FfxivEchoes/Triggers/UpcomingAoePreviewService.cs src/FfxivEchoes/Windows/UpcomingEventsWindow.cs src/FfxivEchoes/Plugin.cs
git commit -m "feat(minimap): タイムライン予測のAoE実効範囲を俯瞰図へ事前描画するサービスを接続"
```

---

### Task 6: 録画 cast_start にキャスト者の位置・向きを記録（案 B の仕込み）

**Files:**
- Modify: `src/FfxivEchoes/Events/GameEvents.cs:45-54`
- Modify: `src/FfxivEchoes/Capture/CastCapture.cs:141-143, 161-163, 195-197`
- Modify: `src/FfxivEchoes/Recording/EventSerializer.cs:100-121`
- Test: `src/FfxivEchoes.Tests/Program.cs`

- [ ] **Step 1: 失敗するテストを書く**

テスト登録リストに追加:

```csharp
    ("CastStartedEvent serializes source world and rotation", CastStartedEvent_SerializesSourceWorldAndRotation),
    ("CastStartedEvent omits source fields when null", CastStartedEvent_OmitsSourceFieldsWhenNull),
```

テスト本体（既存の `CastStartedEvent_SnapshotsTargetWorld`（5168 行付近）の隣に追加）:

```csharp
static void CastStartedEvent_SerializesSourceWorldAndRotation()
{
    var start = DateTimeOffset.Parse("2026-05-06T00:00:00.000Z");
    var ev = new CastStartedEvent(
        start.AddSeconds(3.0), 1001, "Boss", 0x9E00, "両翼斬り", 5.0f, 2001,
        TargetWorld: null,
        SourceWorld: new Vector3(100.5f, 0f, 95.25f),
        SourceRotation: 1.5708f);

    var json = EventSerializer.Serialize(ev, start);

    True(json.Contains("\"source_x\":100.5", StringComparison.Ordinal), $"source_x serialized: {json}");
    True(json.Contains("\"source_z\":95.25", StringComparison.Ordinal), $"source_z serialized: {json}");
    True(json.Contains("\"source_rot\":1.5708", StringComparison.Ordinal), $"source_rot serialized: {json}");
}

static void CastStartedEvent_OmitsSourceFieldsWhenNull()
{
    var start = DateTimeOffset.Parse("2026-05-06T00:00:00.000Z");
    var ev = new CastStartedEvent(start.AddSeconds(3.0), 1001, "Boss", 0x9E00, "両翼斬り", 5.0f, 2001);

    var json = EventSerializer.Serialize(ev, start);

    False(json.Contains("source_x", StringComparison.Ordinal), $"source_x omitted: {json}");
    False(json.Contains("source_rot", StringComparison.Ordinal), $"source_rot omitted: {json}");
}
```

- [ ] **Step 2: テストが失敗することを確認**

```powershell
dotnet build src/FfxivEchoes.Tests -p:Platform=x64
```
期待: `SourceWorld` 引数が存在せず CS1739 等でビルド失敗。

- [ ] **Step 3: CastStartedEvent にフィールド追加**

`GameEvents.cs:45-54`:

```csharp
public sealed record CastStartedEvent(
    DateTimeOffset Timestamp,
    uint SourceId,
    string SourceName,
    uint CastActionId,
    string CastActionName,
    float CastTime,
    uint? TargetId,
    System.Numerics.Vector3? TargetWorld = null,
    System.Numerics.Vector3? SourceWorld = null,
    float? SourceRotation = null
) : IGameEvent;
```

省略可能引数なので既存の呼び出し側（JsonlReplayer.cs:207、Tests 3 箇所）は無修正でコンパイルが通る。

- [ ] **Step 4: CastCapture の 3 箇所でキャスト者の位置・向きを渡す**

`CastCapture.cs` の 141-143 / 161-163 / 195-197 行（`UpdateActor(IBattleNpc actor)` 内）の `new CastStartedEvent(...)` 3 箇所すべてに、末尾へ名前付き引数で追加:

```csharp
new CastStartedEvent(
    DateTimeOffset.UtcNow, sourceId, SrcName(), actionId, name,
    totalCast, target.EntityId, target.World,
    SourceWorld: actor.Position, SourceRotation: actor.Rotation)
```

- [ ] **Step 5: EventSerializer の cast_start に出力を追加**

`EventSerializer.cs` の cast_start ブロック、`TargetWorld` 出力（114-119 行）の直後・`cast_time`（120 行）の前に:

```csharp
                    if (x.SourceWorld is { } sw)
                    {
                        writer.WriteNumber("source_x", Math.Round(sw.X, 3));
                        writer.WriteNumber("source_y", Math.Round(sw.Y, 3));
                        writer.WriteNumber("source_z", Math.Round(sw.Z, 3));
                    }
                    if (x.SourceRotation is { } srot)
                    {
                        writer.WriteNumber("source_rot", Math.Round(srot, 4));
                    }
```

既存の読み手（RecordingAggregationReader 等）は未知フィールドを無視するため互換性影響なし。

- [ ] **Step 6: テストが通ることを確認**

```powershell
dotnet build src/FfxivEchoes/FfxivEchoes.csproj -p:Platform=x64
dotnet run --project src/FfxivEchoes.Tests -p:Platform=x64
```
期待: 0 エラー、新規 2 テスト含め FAIL 0。

- [ ] **Step 7: コミット**

```powershell
git add src/FfxivEchoes/Events/GameEvents.cs src/FfxivEchoes/Capture/CastCapture.cs src/FfxivEchoes/Recording/EventSerializer.cs src/FfxivEchoes.Tests/Program.cs
git commit -m "feat(recording): cast_start にキャスト者の位置・向きを記録（録画位置ベース事前描画の仕込み）"
```

---

### Task 7: 最終検証

- [ ] **Step 1: クリーンビルドと全テスト**

```powershell
dotnet build src/FfxivEchoes/FfxivEchoes.csproj -p:Platform=x64
dotnet run --project src/FfxivEchoes.Tests -p:Platform=x64
```
期待: 0 エラー、FAIL 0。

- [ ] **Step 2: 差分の自己レビュー**

`git log --oneline develop..HEAD` と `git diff develop...HEAD --stat` で全コミットを確認し、以下をチェック:
- 予測 TTS（PredictAdvanceWarningSec）系のコードに触れていないこと
- フレーム毎のファイル I/O・ObjectTable 全走査（0.5s スロットル外）が増えていないこと
- `Publish` 例外がタイムライン描画を巻き込まないこと（try/catch + FrameErrorThrottle）

- [ ] **Step 3: 動作確認はユーザーのゲーム内で実施（手順を提示）**

Claude はゲーム内検証ができないため、以下をユーザーへ依頼する:
1. プラグイン再ビルド・リロード後、シグマ（絶ケフカ）で木人 or 実戦プル
2. タイムライン HUD に予測が出ている状態で、残り 10 秒を切った技の範囲がミニマップにアンバーの薄表示＋「技名 Ns」で出ること
3. 詠唱開始で薄表示が消え、通常の確定表示（赤系）へ切り替わること
4. 設定タブの「予測 AoE を事前表示する」OFF で消えること
5. フレームレートの体感悪化が無いこと（心配なら /xlstats でフレーム時間確認）
