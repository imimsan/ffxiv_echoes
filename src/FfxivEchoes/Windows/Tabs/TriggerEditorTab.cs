using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using FfxivEchoes.Recording;
using FfxivEchoes.Triggers;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Windows.Tabs;

/// <summary>
/// 選択中ゾーンのトリガー編集 + 観測イベント集計ビュー（SPEC.md §7.2）。
/// </summary>
public sealed class TriggerEditorTab : ITab
{
    public string Title => "トリガー編集";
    public string Id => "trigger-editor";

    private readonly TriggerStore _triggerStore;
    private readonly RecordingScanner _recordingScanner;
    private readonly TabContext _tabContext;
    private readonly Events.IEventBus? _eventBus;
    private readonly TriggerAutoGenerator? _autoGenerator;
    private readonly IObjectTable? _objectTable;

    private string? _editingTriggerId;
    private TriggerFile? _workingCopy;
    private string _workingZone = string.Empty;
    private bool _dirty;
    private readonly TimelineRenderer _timelineRenderer = new();
    private bool _aggregateAsTimeline = true;
    private bool _hideSelfEvents = true;
    private bool _hideStatusEvents = false;
    /// <summary>「その他」レーンの環境ノイズ（unknown 型・高頻度 object_appear）を隠すか。</summary>
    private bool _hideEnvironmentalNoise = true;
    private int _attachNoteIndex = -1;
    private TriggerAutoGenerator.GenerationResult? _pendingAutoGen;
    private string? _lastCleanupMessage;

    /// <summary>
    /// 同じ cast_id を持つ auto_cast_* トリガーが複数あるケースを掃除する。
    /// 観測回数（cast_id の集計回数）が分からないので、ID の単純さで優先。
    /// 旧バグ：RecordingAggregationReader が source / target で EventKey を分裂するように
    /// なって以来、auto_cast_XXXX / _source / _source_2 が増殖していた。
    /// </summary>
    /// <returns>削除した件数</returns>
    private int CleanupDuplicateAutoTriggers()
    {
        if (_workingCopy is null) return 0;
        var byCastId = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _workingCopy.Triggers.Count; i++)
        {
            var t = _workingCopy.Triggers[i];
            // auto_ プレフィックスのものだけ対象（手動作成は触らない）
            if (string.IsNullOrEmpty(t.Id) || !t.Id.StartsWith("auto_", StringComparison.OrdinalIgnoreCase)) continue;
            var castId = t.Match?.CastId;
            if (string.IsNullOrEmpty(castId)) continue;
            if (!byCastId.TryGetValue(castId, out var list))
            {
                list = new List<int>();
                byCastId[castId] = list;
            }
            list.Add(i);
        }

        var toRemove = new HashSet<int>();
        foreach (var (_, indices) in byCastId)
        {
            if (indices.Count <= 1) continue;
            // 最も短い ID を「代表」として残す（auto_cast_XXXX が最短、 _source_2 は長い）
            var keep = indices[0];
            var keepLen = _workingCopy.Triggers[keep].Id.Length;
            foreach (var idx in indices)
            {
                var len = _workingCopy.Triggers[idx].Id.Length;
                if (len < keepLen)
                {
                    keep = idx;
                    keepLen = len;
                }
            }
            foreach (var idx in indices)
            {
                if (idx != keep) toRemove.Add(idx);
            }
        }

        if (toRemove.Count == 0) return 0;
        // 後ろから消す（インデックスずれ防止）
        var sorted = toRemove.OrderByDescending(x => x).ToArray();
        foreach (var idx in sorted)
        {
            _workingCopy.Triggers.RemoveAt(idx);
        }
        _dirty = true;
        return sorted.Length;
    }
    /// <summary>同じ「録画から自動生成」操作で生成される攻略メカニクスの下書き。trigger と一緒に適用する。</summary>
    private StrategyDraftGenerator.GenerationResult? _pendingStrategyDrafts;
    /// <summary>strategy drafts の追加先プロファイル（trigger と同じ working copy 上）</summary>
    private StrategyProfile? _pendingStrategyProfile;
    private string? _attachStrategyProfileId;
    private string? _attachStrategyMechanicId;
    private string? _lastStrategyDraftMessage;
    private long _seenStoreReloadVersion = -1;
    private bool _externalStoreChangedWhileDirty;

    // ID 選択 popup の状態。録画 aggregate から「名前: ID」リストでクリック選択させる
    private string? _idPickerType;            // "cast_start" / "status_gain" / "action_used"
    private Action<string?, string?>? _idPickerCallback; // (id, name) を受け取る
    private string _idPickerSearch = string.Empty;
    private bool _shouldOpenIdPicker;          // OpenPopup を popup 描画と同じ ID stack 階層で実行するためのフラグ
    // popup の ID は ## プレフィックス無しの単純な文字列にする（## はラベル隠しの記法で
    // popup ID では使わない方が確実）
    private const string IdPickerPopupId = "id-picker-popup";

    // ─── 分岐自動検出 popup の状態 ────────────────────────────
    private FfxivEchoes.Recording.BranchDetectionResult? _branchDetectionResult;
    private bool[] _branchDetectionGroupSelected = System.Array.Empty<bool>();
    private bool _shouldOpenBranchPopup;
    private bool _branchDetectionGenerateMechanics = true;
    private string? _branchApplyResultMessage;
    private const string BranchDetectionPopupId = "branch-detection-popup";

    /// <summary>「アリーナ寸法ルーラーを開く」ボタンが押されたときに呼ぶ。Plugin 側から差し込む。</summary>
    private readonly Action? _openArenaRuler;

    public TriggerEditorTab(TriggerStore triggerStore, RecordingScanner scanner, TabContext context,
        Events.IEventBus? eventBus = null, TriggerAutoGenerator? autoGenerator = null,
        IObjectTable? objectTable = null,
        Action? openArenaRuler = null)
    {
        _triggerStore = triggerStore;
        _recordingScanner = scanner;
        _tabContext = context;
        _eventBus = eventBus;
        _autoGenerator = autoGenerator;
        _objectTable = objectTable;
        _openArenaRuler = openArenaRuler;
    }

    public void Draw()
    {
        var zone = _tabContext.SelectedZone;
        if (string.IsNullOrEmpty(zone))
        {
            ResetWorkingCopy();
            ImGui.TextWrapped("コンテンツ一覧から「編集」を押してゾーンを選択してください。");
            return;
        }

        // ゾーンが切り替わったら作業コピーを再構築
        if (_workingZone != zone)
        {
            LoadWorkingCopy(zone);
        }
        else
        {
            SynchronizeWorkingCopyWithStore(zone);
        }
        if (_workingCopy is null && _tabContext.PendingCreateFromRecording)
        {
            _workingCopy = CreateStarterFile(zone, fromRecording: true);
            _workingZone = zone;
            _tabContext.PendingCreateFromRecording = false;
            _dirty = true;
            _externalStoreChangedWhileDirty = false;
            _seenStoreReloadVersion = _triggerStore.ReloadVersion;
        }
        if (_workingCopy is null)
        {
            ImGui.TextWrapped($"ゾーン \"{zone}\" のトリガー定義は未作成です。下のボタンで新規作成できます。");
            if (ImGui.Button("新規作成"))
            {
                _workingCopy = CreateStarterFile(zone, _tabContext.PendingCreateFromRecording);
                _workingZone = zone;
                _tabContext.PendingCreateFromRecording = false;
                _dirty = true;
                _externalStoreChangedWhileDirty = false;
                _seenStoreReloadVersion = _triggerStore.ReloadVersion;
            }
            return;
        }

        DrawHeader(zone);
        ImGui.Separator();

        if (ImGui.BeginTabBar("##trigger-editor-subtabs"))
        {
            if (ImGui.BeginTabItem("トリガー一覧"))
            {
                DrawTriggerListPanel();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("集計（観測イベント）"))
            {
                DrawAggregatePanel(zone);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("ファイル設定"))
            {
                DrawFileSettingsPanel();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("攻略登録"))
            {
                DrawStrategyPanel();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("ノート"))
            {
                DrawNotesPanel();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("バックアップ"))
            {
                DrawBackupsPanel(zone);
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    private void LoadWorkingCopy(string zone)
    {
        var existing = _triggerStore.GetByZone(zone);
        _workingCopy = existing is null
            ? null
            : Clone(existing);
        _workingZone = zone;
        _editingTriggerId = null;
        _dirty = false;
        _externalStoreChangedWhileDirty = false;
        _profileCenterDragMode = false;
        _centerClickModeMechId = null;
        _seenStoreReloadVersion = _triggerStore.ReloadVersion;
    }

    private void ResetWorkingCopy()
    {
        _workingCopy = null;
        _workingZone = string.Empty;
        _editingTriggerId = null;
        _dirty = false;
        _externalStoreChangedWhileDirty = false;
        _profileCenterDragMode = false;
        _centerClickModeMechId = null;
        _seenStoreReloadVersion = _triggerStore.ReloadVersion;
    }

    private void SynchronizeWorkingCopyWithStore(string zone)
    {
        var currentVersion = _triggerStore.ReloadVersion;
        if (_seenStoreReloadVersion == currentVersion)
        {
            return;
        }

        if (_dirty)
        {
            if (_workingCopy is not null && MergeLearnedObjectAoeRules(_triggerStore.GetByZone(zone), _workingCopy))
            {
                _dirty = true;
            }
            _externalStoreChangedWhileDirty = true;
            _seenStoreReloadVersion = currentVersion;
            return;
        }

        LoadWorkingCopy(zone);
    }

    private static bool MergeLearnedObjectAoeRules(TriggerFile? source, TriggerFile target)
    {
        if (source is null)
        {
            return false;
        }

        var changed = false;
        foreach (var sourceProfile in source.StrategyProfiles)
        {
            var targetProfile = target.StrategyProfiles.FirstOrDefault(p =>
                string.Equals(p.Id, sourceProfile.Id, StringComparison.OrdinalIgnoreCase));
            if (targetProfile is null)
            {
                continue;
            }

            foreach (var rule in sourceProfile.ObjectAoeRules)
            {
                if (!IsLearnedObjectAoeRule(rule.Source))
                {
                    continue;
                }

                if (targetProfile.ObjectAoeRules.Any(existing =>
                        string.Equals(existing.Id, rule.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                targetProfile.ObjectAoeRules.Add(CloneObjectAoeRule(rule));
                changed = true;
            }
        }

        return changed;
    }

    private static bool IsLearnedObjectAoeRule(string? source)
    {
        return (source ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "recording" or "dictionary" or "builtin" => true,
            _ => false,
        };
    }

    private static ObjectAoeRule CloneObjectAoeRule(ObjectAoeRule src)
    {
        return new ObjectAoeRule
        {
            Id = src.Id,
            Enabled = src.Enabled,
            ObjectName = src.ObjectName,
            NameMatch = src.NameMatch,
            DataId = src.DataId,
            Label = src.Label,
            Source = src.Source,
            Shape = src.Shape,
            RadiusM = src.RadiusM,
            InnerRadiusM = src.InnerRadiusM,
            FanDeg = src.FanDeg,
            HalfWidthM = src.HalfWidthM,
            DurationSec = src.DurationSec,
            Color = src.Color,
            LiveFloorPaint = src.LiveFloorPaint,
        };
    }

    private static TriggerFile CreateStarterFile(string zone, bool fromRecording)
    {
        var now = DateTimeOffset.UtcNow;
        var file = new TriggerFile
        {
            Zone = zone,
            ActiveStrategyProfileId = "default",
            Metadata = new TriggerFileMetadata
            {
                CreatedAt = now,
                LastModified = now,
                Notes = fromRecording
                    ? "Created from recordings. Keep recording pulls to improve the learned timeline."
                    : "Created manually.",
            },
        };

        file.AutoSettings.EnableTriggers = true;
        file.AutoSettings.AutoRecord = fromRecording;
        file.AutoSettings.ShowTimeline = true;
        file.AutoSettings.ShowPredictedCasts = true;
        // 既定 ON：ボスキャストの AoE 範囲を Lumina から実寸で描画する
        // （AutoTelegraphService 経路。AddObjectAoeService の add 推測描画は別途無効）。
        // Lumina に EffectRange があるキャストだけ実寸描画する。
        // 不明キャストは適当な既定円を出さず、攻略登録の AoE Zone で手動補正する。
        file.AutoSettings.ShowAutoTelegraphs = true;
        file.AutoSettings.ShowAllEnemyCasts = false;
        file.AutoSettings.ShowAutoAttacks = true;
        file.AutoSettings.PredictAdvanceWarningSec = AutoSettings.DefaultPredictAdvanceWarningSec;
        file.StrategyProfiles.Add(CreateDefaultStrategyProfile("default", "Default party strategy"));
        return file;
    }

    private static StrategyProfile CreateDefaultStrategyProfile(string id, string name)
    {
        return new StrategyProfile
        {
            Id = id,
            Name = name,
            ArenaRadius = 20.0,
            SpreadPositions = CreateEightWaySpreadPositions(),
        };
    }

    private static readonly string[] ArenaShapeOptions = new[] { "circle", "square", "rect" };
    private static readonly string[] ArenaShapeLabels = new[] { "円形", "正方形", "長方形" };

    /// <summary>
    /// メカニクス単位のアリーナ形状上書き UI。「フェーズで地形が変わる」想定で、
    /// プロファイル既定とは異なる形状／寸法を持てる。空欄なら継承。
    /// </summary>
    private void DrawMechanicArenaShapeOverride(StrategyProfile profile, MechanicStrategy mechanic)
    {
        // 中心設定は折りたたみの外に常に出す（よく使うので埋もれてはいけない）
        ImGui.Spacing();
        ImGui.TextUnformatted("このギミック専用の中心:");
        ImGui.SameLine();
        var hasMechCenter = mechanic.ArenaCenterX.HasValue && mechanic.ArenaCenterZ.HasValue;
        if (hasMechCenter)
        {
            ImGui.TextDisabled($"({mechanic.ArenaCenterX:0.0}, {mechanic.ArenaCenterZ:0.0})");
        }
        else
        {
            ImGui.TextDisabled("未設定（フェーズ → プロファイル既定を継承）");
        }
        ImGui.SameLine();
        if (ImGui.SmallButton($"既定を適用##mech-arena-apply-profile-{mechanic.Id}"))
        {
            if (MechanicArenaDefaultsPolicy.ApplyProfileArena(profile, mechanic))
            {
                _dirty = true;
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("プロファイル基本情報のアリーナ形状・寸法・中心を、このギミックへコピーする。\n" +
                              "正方形化や中心補正をあとからまとめて反映したい時に使う。");
        }
        ImGui.SameLine();
        var canCalibrateMech = _objectTable?.LocalPlayer is not null;
        if (!canCalibrateMech) ImGui.BeginDisabled();
        if (ImGui.SmallButton($"ここを中心##mech-here-top-{mechanic.Id}"))
        {
            var p = _objectTable!.LocalPlayer!.Position;
            mechanic.ArenaCenterX = p.X;
            mechanic.ArenaCenterZ = p.Z;
            _dirty = true;
        }
        if (canCalibrateMech && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("いま自分が立っている世界座標をこのギミック専用の中心として記録。\n" +
                              "フェーズで地形が動くケースに使う。");
        }
        if (!canCalibrateMech) ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.SmallButton($"クリア##mech-center-clear-top-{mechanic.Id}"))
        {
            mechanic.ArenaCenterX = null;
            mechanic.ArenaCenterZ = null;
            _dirty = true;
        }
        ImGui.SameLine();
        var cxTop = (float)(mechanic.ArenaCenterX ?? 0);
        ImGui.SetNextItemWidth(90f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputFloat($"X##mech-cx-top-{mechanic.Id}", ref cxTop, 0.5f, 1f, "%.1f"))
        {
            mechanic.ArenaCenterX = cxTop;
            _dirty = true;
        }
        ImGui.SameLine();
        var czTop = (float)(mechanic.ArenaCenterZ ?? 0);
        ImGui.SetNextItemWidth(90f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputFloat($"Z##mech-cz-top-{mechanic.Id}", ref czTop, 0.5f, 1f, "%.1f"))
        {
            mechanic.ArenaCenterZ = czTop;
            _dirty = true;
        }

        // 「中心調整モード」：これを ON にすると、下のキャンバス上でドラッグ / ダブルクリック
        // した分だけこのギミック専用の中心座標を動かす。
        ImGui.SameLine();
        var clickMode = _centerClickModeMechId == mechanic.Id;
        if (clickMode)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.7f, 0.4f, 0.2f, 1f));
        }
        if (ImGui.SmallButton($"{(clickMode ? "■" : "○")} ドラッグで中心##mech-click-mode-{mechanic.Id}"))
        {
            _centerClickModeMechId = clickMode ? null : mechanic.Id;
            if (!clickMode)
            {
                _profileCenterDragMode = false;
            }
        }
        if (clickMode)
        {
            ImGui.PopStyleColor();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "ON にすると、下の散開ポジ地図をドラッグして\n" +
                "このギミック専用の中心座標を微調整できる。\n" +
                "D1/D2 などの相対配置は動かさず、中心だけを動かす。\n" +
                "空白ダブルクリックでも、その地点ぶん中心を動かせる。");
        }

        // 形状・寸法は折りたたみで（基本はプロファイル既定で OK なので普段は閉じてる）
        ImGui.Spacing();
        // 「★上書き中」は mechanic 値が *プロファイル* と実質的に違う場合のみ表示する。
        // 自動生成や cascade で profile 値がコピーされているだけなら上書きとは扱わない
        // （見た目には同じ寸法なので「★上書き中」表示は誤解を招く）。
        var hasOverride =
            (mechanic.ArenaShape is not null && !string.Equals(mechanic.ArenaShape, profile.ArenaShape, StringComparison.OrdinalIgnoreCase)) ||
            (mechanic.ArenaRadius.HasValue && mechanic.ArenaRadius != profile.ArenaRadius) ||
            (mechanic.ArenaWidth.HasValue && mechanic.ArenaWidth != profile.ArenaWidth) ||
            (mechanic.ArenaDepth.HasValue && mechanic.ArenaDepth != profile.ArenaDepth) ||
            (mechanic.ArenaCenterX.HasValue && mechanic.ArenaCenterX != profile.ArenaCenterX) ||
            (mechanic.ArenaCenterZ.HasValue && mechanic.ArenaCenterZ != profile.ArenaCenterZ);
        var headerLabel = hasOverride
            ? "アリーナ形状・寸法・中心（このギミック専用）★上書き中"
            : "アリーナ形状・寸法・中心（このギミック専用）— 既定はプロファイル";
        if (!ImGui.CollapsingHeader(headerLabel))
        {
            return;
        }

        var shapes = new[] { "（プロファイル継承）" }
            .Concat(ArenaShapeLabels).ToArray();
        var shapeKeys = new[] { (string?)null }.Concat(ArenaShapeOptions.Select(s => (string?)s)).ToArray();

        var idx = 0;
        for (var k = 1; k < shapeKeys.Length; k++)
        {
            if (string.Equals(shapeKeys[k], mechanic.ArenaShape, StringComparison.OrdinalIgnoreCase))
            {
                idx = k;
                break;
            }
        }
        ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
        if (ImGui.Combo($"形状##mech-shape-{mechanic.Id}", ref idx, shapes, shapes.Length))
        {
            mechanic.ArenaShape = shapeKeys[idx];
            _dirty = true;
        }

        var effectiveShape = mechanic.ArenaShape ?? profile.ArenaShape ?? "circle";
        if (string.Equals(effectiveShape, "circle", StringComparison.OrdinalIgnoreCase))
        {
            var rad = (float)(mechanic.ArenaRadius ?? profile.ArenaRadius ?? 20.0);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
            if (ImGui.InputFloat($"半径(m)##mech-rad-{mechanic.Id}", ref rad, 0.5f, 1f, "%.1f"))
            {
                mechanic.ArenaRadius = rad <= 0 ? null : rad;
                _dirty = true;
            }
        }
        else
        {
            var w = (float)(mechanic.ArenaWidth ?? profile.ArenaWidth ?? (profile.ArenaRadius ?? 20.0) * 2);
            var d = (float)(mechanic.ArenaDepth ?? profile.ArenaDepth ?? (profile.ArenaRadius ?? 20.0) * 2);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(110f * ImGuiHelpers.GlobalScale);
            if (ImGui.InputFloat($"幅(東西m)##mech-w-{mechanic.Id}", ref w, 0.5f, 1f, "%.1f"))
            {
                mechanic.ArenaWidth = w <= 0 ? null : w;
                _dirty = true;
            }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(110f * ImGuiHelpers.GlobalScale);
            if (ImGui.InputFloat($"奥行(南北m)##mech-d-{mechanic.Id}", ref d, 0.5f, 1f, "%.1f"))
            {
                mechanic.ArenaDepth = d <= 0 ? null : d;
                _dirty = true;
            }
        }

        ImGui.SameLine();
        if (ImGui.SmallButton($"既定にリセット##mech-shape-reset-{mechanic.Id}"))
        {
            mechanic.ArenaShape = null;
            mechanic.ArenaRadius = null;
            mechanic.ArenaWidth = null;
            mechanic.ArenaDepth = null;
            mechanic.ArenaCenterX = null;
            mechanic.ArenaCenterZ = null;
            _dirty = true;
        }
        // 中心 UI は折りたたみ外（上部）に常設しているのでここは出さない
    }

    /// <summary>
    /// アリーナ形状（円・正方形・長方形）と寸法を編集し、ゲーム内位置を使った
    /// 中心 / 端のキャリブレーションを提供する。
    /// </summary>
    /// <summary>
    /// アリーナ寸法が未設定なら、録画 aggregate から 1 度だけ自動推定して埋める。
    /// 「メートルとか分からない」「ユーザーに数値を打たせるのは難しい」を解消する。
    /// </summary>
    private readonly HashSet<string> _arenaAutoEstimatedZones = new(StringComparer.OrdinalIgnoreCase);

    private void TryAutoEstimateArena(StrategyProfile profile)
    {
        var key = $"{_workingZone}::{profile.Id}";
        if (_arenaAutoEstimatedZones.Contains(key)) return;

        var alreadyConfigured =
            profile.ArenaCenterX.HasValue ||
            profile.ArenaCenterZ.HasValue ||
            profile.ArenaRadius.HasValue ||
            profile.ArenaWidth.HasValue ||
            profile.ArenaDepth.HasValue;
        if (alreadyConfigured)
        {
            _arenaAutoEstimatedZones.Add(key); // 既に手動設定済 → 再推定しない
            return;
        }

        try
        {
            var est = _recordingScanner.EstimateArenaFromRecordings(_workingZone);
            if (est is null) return; // 録画ゼロ → ユーザー次回ログイン時に再試行
            profile.ArenaCenterX = est.CenterX;
            profile.ArenaCenterZ = est.CenterZ;
            // 円形は半径、矩形は幅×奥行を埋める
            if (string.Equals(profile.ArenaShape, "circle", StringComparison.OrdinalIgnoreCase))
            {
                profile.ArenaRadius = est.Radius;
            }
            else
            {
                profile.ArenaWidth = est.Width;
                profile.ArenaDepth = est.Depth;
                // circle 値も埋めておく（後でユーザーが形状を変えたとき用）
                profile.ArenaRadius = est.Radius;
            }
            _dirty = true;
            _arenaAutoEstimatedZones.Add(key);
        }
        catch
        {
            _arenaAutoEstimatedZones.Add(key); // 失敗時も毎フレーム試さない
        }
    }

    private void DrawArenaShapeAndCalibration(StrategyProfile profile)
    {
        TryAutoEstimateArena(profile);
        if (_arenaAutoEstimatedZones.Contains($"{_workingZone}::{profile.Id}") &&
            (profile.ArenaCenterX.HasValue || profile.ArenaRadius.HasValue))
        {
            // 自動推定が走ったときの簡素な案内
            ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.6f, 0.85f, 0.6f, 1f));
            ImGui.TextWrapped("ℹ アリーナ寸法は録画から自動推定済（必要なら下で手動修正）");
            ImGui.PopStyleColor();
        }
        var shapeIdx = Math.Max(0, Array.IndexOf(ArenaShapeOptions, profile.ArenaShape ?? "circle"));
        ImGui.SetNextItemWidth(160f * ImGuiHelpers.GlobalScale);
        if (ImGui.Combo("アリーナ形状##arena-shape", ref shapeIdx, ArenaShapeLabels, ArenaShapeLabels.Length))
        {
            profile.ArenaShape = ArenaShapeOptions[shapeIdx];
            _dirty = true;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("円形：中心からの半径（極コンテンツの大半）\n" +
                              "正方形：一辺の長さ（中心から半分が有効）\n" +
                              "長方形：横幅×縦幅");
        }

        if (string.Equals(profile.ArenaShape, "circle", StringComparison.OrdinalIgnoreCase))
        {
            var radius = (float)(profile.ArenaRadius ?? 20.0);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
            if (ImGui.InputFloat("半径(m)##strategy-arena-radius", ref radius, 0.5f, 1.0f, "%.1f"))
            {
                profile.ArenaRadius = radius <= 0 ? null : radius;
                _dirty = true;
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("極/絶のおおむね 18-25m。下の「端をマーク」で実測可能");
        }
        else
        {
            var width = (float)(profile.ArenaWidth ?? (profile.ArenaRadius ?? 20.0) * 2);
            var depth = (float)(profile.ArenaDepth ?? (profile.ArenaRadius ?? 20.0) * 2);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
            if (ImGui.InputFloat("幅(東西m)##arena-w", ref width, 0.5f, 1.0f, "%.1f"))
            {
                profile.ArenaWidth = width <= 0 ? null : width;
                _dirty = true;
            }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
            if (ImGui.InputFloat("奥行(南北m)##arena-d", ref depth, 0.5f, 1.0f, "%.1f"))
            {
                profile.ArenaDepth = depth <= 0 ? null : depth;
                _dirty = true;
            }
        }

        ImGui.SameLine();
        var hasMechanics = profile.Mechanics.Count > 0;
        if (!hasMechanics) ImGui.BeginDisabled();
        if (ImGui.SmallButton("形状・中心を全ギミックへ適用##arena-apply-to-all-mechanics"))
        {
            var changedCount = MechanicArenaDefaultsPolicy.ApplyProfileArenaToAll(profile, profile.Mechanics);
            if (changedCount > 0)
            {
                _dirty = true;
            }
        }
        if (!hasMechanics) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("現在のプロファイルのアリーナ形状・寸法・中心を、既存の全ギミックへコピーする。\n" +
                              "正方形/長方形や中心位置をあとから直したときの一括反映用。");
        }

        // アリーナ中心の表示・キャリブレーション
        var hasCenter = profile.ArenaCenterX.HasValue && profile.ArenaCenterZ.HasValue;
        if (hasCenter)
        {
            ImGui.TextDisabled($"中心: ({profile.ArenaCenterX:0.0}, {profile.ArenaCenterZ:0.0})");
        }
        else
        {
            ImGui.TextDisabled("中心: 未設定（戦闘開始時のボス位置を自動採用）");
        }

        ImGui.SameLine();
        var canCalibrate = _objectTable?.LocalPlayer is not null;
        if (!canCalibrate) ImGui.BeginDisabled();
        if (ImGui.SmallButton("ここを中心にする##arena-set-center"))
        {
            var p = _objectTable!.LocalPlayer!.Position;
            profile.ArenaCenterX = p.X;
            profile.ArenaCenterZ = p.Z;
            _dirty = true;
        }
        if (canCalibrate && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("いま自分が立っている位置をアリーナ中心として記録する。\n" +
                              "アリーナ真ん中に立ってからクリック。\n" +
                              "この中心は自動生成ミニマップ / 自動AoE表示にも使われる。");
        }
        else if (!canCalibrate && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("ゲーム内に居ないとキャリブレーションできない");
        }

        ImGui.SameLine();
        if (!canCalibrate) ImGui.EndDisabled();
        if (!canCalibrate) ImGui.BeginDisabled();
        if (ImGui.SmallButton("端をマーク##arena-set-edge"))
        {
            var p = _objectTable!.LocalPlayer!.Position;
            // 中心が未設定なら現在位置を中心に「半径＝0」、ユーザーは中心 → 端の順で押すフロー
            if (!profile.ArenaCenterX.HasValue || !profile.ArenaCenterZ.HasValue)
            {
                profile.ArenaCenterX = p.X;
                profile.ArenaCenterZ = p.Z;
            }
            else
            {
                var dx = p.X - profile.ArenaCenterX.Value;
                var dz = p.Z - profile.ArenaCenterZ.Value;
                var dist = (float)Math.Sqrt(dx * dx + dz * dz);
                if (string.Equals(profile.ArenaShape, "circle", StringComparison.OrdinalIgnoreCase))
                {
                    profile.ArenaRadius = Math.Round(dist, 1);
                }
                else
                {
                    // 端をマークしたら、中心からの差分を「半幅」「半奥行」にスナップ
                    profile.ArenaWidth = Math.Round(Math.Abs(dx) * 2, 1);
                    profile.ArenaDepth = Math.Round(Math.Abs(dz) * 2, 1);
                }
            }
            _dirty = true;
        }
        if (canCalibrate && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("中心を先に設定 → アリーナの端まで歩いて押す。\n" +
                              "中心からの距離をもとに半径（円）または半幅×半奥行（矩形）を自動計算。");
        }
        if (!canCalibrate) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.SmallButton("中心をクリア##arena-clear-center"))
        {
            profile.ArenaCenterX = null;
            profile.ArenaCenterZ = null;
            _dirty = true;
        }
        ImGui.SameLine();
        ImGui.TextDisabled("下の中心調整マップをドラッグ");

        ImGui.SameLine();
        if (ImGui.SmallButton("録画から推定##arena-estimate"))
        {
            try
            {
                var (est, diag) = _recordingScanner.EstimateArenaWithDiagnostics(_workingZone);
                _arenaEstimateDiag = diag;
                if (est is null)
                {
                    _shouldOpenArenaEstimateDiag = true;
                }
                else
                {
                    profile.ArenaCenterX = est.CenterX;
                    profile.ArenaCenterZ = est.CenterZ;
                    if (string.Equals(profile.ArenaShape, "circle", StringComparison.OrdinalIgnoreCase))
                    {
                        profile.ArenaRadius = est.Radius;
                    }
                    else
                    {
                        profile.ArenaWidth = est.Width;
                        profile.ArenaDepth = est.Depth;
                    }
                    _dirty = true;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "[FfxivEchoes] アリーナ推定失敗");
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("このゾーンの録画にある object_appear 座標を集めて bounding box から\n" +
                              "中心・サイズを推定する。失敗したら詳細な原因が popup に出る。");
        }

        // 録画が拾えない場合の即時フォールバック：プレイヤーの現在位置を中心に既定半径
        ImGui.SameLine();
        var canSelfEstimate = _objectTable?.LocalPlayer is not null;
        if (!canSelfEstimate) ImGui.BeginDisabled();
        if (ImGui.SmallButton("自分位置から推定##self-estimate"))
        {
            var p = _objectTable!.LocalPlayer!.Position;
            profile.ArenaCenterX = p.X;
            profile.ArenaCenterZ = p.Z;
            if (string.Equals(profile.ArenaShape, "circle", StringComparison.OrdinalIgnoreCase))
            {
                profile.ArenaRadius ??= 20.0;
            }
            else
            {
                profile.ArenaWidth ??= 40.0;
                profile.ArenaDepth ??= 40.0;
            }
            _dirty = true;
        }
        if (canSelfEstimate && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("いま自分が立ってる場所をアリーナ中心、半径 20m（円）または 40×40m（矩形）として記録。\n" +
                              "アリーナの真ん中で押すこと。録画ゼロでも使える緊急用。");
        }
        if (!canSelfEstimate) ImGui.EndDisabled();

        // ルーラーウィンドウを開く（マーカー間距離 + 同心円オーバーレイ）
        if (_openArenaRuler is not null)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("ルーラーを開く##open-ruler"))
            {
                _openArenaRuler();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "アリーナ寸法計測ウィンドウを開く。\n" +
                    "練習部屋で自分の周りに 5m きざみ最大 40m の同心円を描画 + マーカー間距離をライブ表示。\n" +
                    "（戦闘 1 回終わると自動的に寸法が入るので、普段はこれを使わなくて OK）");
            }
        }
        ImGui.TextDisabled(
            "💡 戦闘 1 回終わると、プレイヤーの動きから自動でアリーナ寸法が入る\n" +
            "   （手動で寸法／中心を入れたプロファイルは上書きされない）");

        DrawArenaCenterCalibrationMap(profile);

        // 診断 popup（OpenPopup と BeginPopupModal は同階層から呼ぶ）
        if (_shouldOpenArenaEstimateDiag)
        {
            _shouldOpenArenaEstimateDiag = false;
            ImGui.OpenPopup("##arena-estimate-diag");
        }
        ImGui.SetNextWindowSize(new Vector2(540f * ImGuiHelpers.GlobalScale, 0f), ImGuiCond.Appearing);
        if (ImGui.BeginPopupModal("##arena-estimate-diag", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted("録画からのアリーナ推定が失敗しました");
            ImGui.Separator();
            if (_arenaEstimateDiag is { } d)
            {
                ImGui.TextWrapped($"ゾーン: {d.Zone}");
                ImGui.TextWrapped($"録画ファイル数（このゾーン）: {d.RecordingsByZone}");
                ImGui.TextWrapped($"録画ファイル数（fallback で全件）: {d.RecordingsFlat}");
                ImGui.TextWrapped($"player_pos 観測数: {d.PlayerPosCount}（自分の位置サンプル / アリーナ寸法の主役データ）");
                ImGui.TextWrapped($"object_appear 観測数: {d.ObjectAppearCount}（フォールバック用）");
                ImGui.TextWrapped($"採用ソース: {d.SourceUsed}");
                ImGui.TextWrapped($"有効座標サンプル数: {d.ValidSampleCount}");
                ImGui.Separator();
                if (!string.IsNullOrEmpty(d.FailureReason))
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.7f, 0.4f, 1f));
                    ImGui.TextWrapped(d.FailureReason);
                    ImGui.PopStyleColor();
                }
                ImGui.Separator();
                ImGui.TextWrapped("代わりに「自分位置から推定」ボタンを使えば、現在の自分の位置を中心として手早く設定できます。");
            }
            ImGui.Spacing();
            if (ImGui.Button("OK")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

    private RecordingScanner.ArenaEstimateDiagnostics? _arenaEstimateDiag;
    private bool _shouldOpenArenaEstimateDiag;

    private void DrawArenaCenterCalibrationMap(StrategyProfile profile)
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("中心調整マップ");
        ImGui.SameLine();
        ImGui.TextDisabled("ドラッグ / 空白ダブルクリックで中心を合わせる");

        var (halfW0, halfD0) = ResolveArenaHalfExtents(profile);
        var halfW = (float)halfW0;
        var halfD = (float)halfD0;
        if (halfW <= 0) halfW = 20f;
        if (halfD <= 0) halfD = 20f;

        var shape = profile.ArenaShape ?? "circle";
        var canvasSize = 240f * ImGuiHelpers.GlobalScale;
        var pos = ImGui.GetCursorScreenPos();
        var center = new Vector2(pos.X + canvasSize * 0.5f, pos.Y + canvasSize * 0.5f);
        var mapHalf = canvasSize * 0.5f - 8f * ImGuiHelpers.GlobalScale;
        var pxPerMeterX = mapHalf / halfW;
        var pxPerMeterZ = mapHalf / halfD;
        var draw = ImGui.GetWindowDrawList();

        if (string.Equals(shape, "circle", StringComparison.OrdinalIgnoreCase))
        {
            draw.AddCircleFilled(center, mapHalf, 0xC0181C25, 64);
            draw.AddCircle(center, mapHalf, 0x90FFFFFF, 64, 1.2f);
        }
        else
        {
            var rectMin = new Vector2(center.X - halfW * pxPerMeterX, center.Y - halfD * pxPerMeterZ);
            var rectMax = new Vector2(center.X + halfW * pxPerMeterX, center.Y + halfD * pxPerMeterZ);
            draw.AddRectFilled(rectMin, rectMax, 0xC0181C25);
            draw.AddRect(rectMin, rectMax, 0x90FFFFFF, 0f, ImDrawFlags.None, 1.2f);
        }

        const float gridStep = 5f;
        for (var m = gridStep; m < Math.Max(halfW, halfD); m += gridStep)
        {
            var rxPx = pxPerMeterX * m;
            var rzPx = pxPerMeterZ * m;
            if (string.Equals(shape, "circle", StringComparison.OrdinalIgnoreCase))
            {
                if (m < halfW) draw.AddCircle(center, rxPx, 0x35FFFFFF, 48, 0.8f);
            }
            else
            {
                if (m < halfW)
                {
                    draw.AddLine(new Vector2(center.X + rxPx, center.Y - halfD * pxPerMeterZ),
                        new Vector2(center.X + rxPx, center.Y + halfD * pxPerMeterZ), 0x30FFFFFF, 0.7f);
                    draw.AddLine(new Vector2(center.X - rxPx, center.Y - halfD * pxPerMeterZ),
                        new Vector2(center.X - rxPx, center.Y + halfD * pxPerMeterZ), 0x30FFFFFF, 0.7f);
                }
                if (m < halfD)
                {
                    draw.AddLine(new Vector2(center.X - halfW * pxPerMeterX, center.Y + rzPx),
                        new Vector2(center.X + halfW * pxPerMeterX, center.Y + rzPx), 0x30FFFFFF, 0.7f);
                    draw.AddLine(new Vector2(center.X - halfW * pxPerMeterX, center.Y - rzPx),
                        new Vector2(center.X + halfW * pxPerMeterX, center.Y - rzPx), 0x30FFFFFF, 0.7f);
                }
            }
        }

        draw.AddLine(new Vector2(center.X, center.Y - mapHalf), new Vector2(center.X, center.Y + mapHalf), 0x45FFFFFF, 0.8f);
        draw.AddLine(new Vector2(center.X - mapHalf, center.Y), new Vector2(center.X + mapHalf, center.Y), 0x45FFFFFF, 0.8f);
        AddCenteredMapText(draw, new Vector2(center.X, pos.Y + 4f), "N", 0xFFCCCCCC);
        AddCenteredMapText(draw, new Vector2(center.X, pos.Y + canvasSize - 16f), "S", 0xFFCCCCCC);
        AddCenteredMapText(draw, new Vector2(pos.X + 8f, center.Y - 7f), "W", 0xFFCCCCCC);
        AddCenteredMapText(draw, new Vector2(pos.X + canvasSize - 16f, center.Y - 7f), "E", 0xFFCCCCCC);

        var dimText = string.Equals(shape, "circle", StringComparison.OrdinalIgnoreCase)
            ? $"r{halfW:0}m"
            : $"{halfW * 2:0}x{halfD * 2:0}m";
        AddCenteredMapText(draw, new Vector2(pos.X + 32f, pos.Y + 14f), dimText, 0xFFFFD080u);

        DrawWaymarkOverlay(draw, profile, null, center, pxPerMeterX, pxPerMeterZ, halfW, halfD, mapHalf);
        DrawLocalPlayerOverlay(draw, profile, null, center, pxPerMeterX, pxPerMeterZ, halfW, halfD);
        DrawCenterDragGuide(draw, center, mapHalf);

        ImGui.SetCursorScreenPos(pos);
        ImGui.InvisibleButton("##arena-center-calibration-map", new Vector2(canvasSize, canvasSize));
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        if (active && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0.0f))
        {
            var delta = ImGui.GetIO().MouseDelta;
            if (delta.X != 0 || delta.Y != 0)
            {
                ArenaCenterDragPolicy.ApplyProfileCenterDelta(
                    profile,
                    delta.X / pxPerMeterX,
                    delta.Y / pxPerMeterZ);
                _dirty = true;
            }
        }
        if (hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            var mouse = ImGui.GetMousePos();
            ArenaCenterDragPolicy.ApplyProfileCenterDelta(
                profile,
                (mouse.X - center.X) / pxPerMeterX,
                (mouse.Y - center.Y) / pxPerMeterZ);
            _dirty = true;
        }
        if (hovered)
        {
            ImGui.SetTooltip("この地図だけで中心調整できます。\nドラッグ: 微調整\n空白ダブルクリック: その地点を中心へ寄せる");
        }

        ImGui.SetCursorScreenPos(new Vector2(pos.X, pos.Y + canvasSize));
        ImGui.Dummy(new Vector2(canvasSize, 0f));
        ImGui.TextDisabled("YOU とフィールドマーカーが中央に合うように、地図の背景をドラッグしてください。");
    }

    /// <summary>「クリックで中心」モードが有効なメカニクスの Id。null なら全メカニクスで OFF。</summary>
    /// <remarks>
    /// ON 中は <see cref="DrawSpreadPositionMapEditor"/> のダブルクリックが
    /// 「ポジ追加」ではなく「アリーナ中心移動」として解釈される。
    /// </remarks>
    private string? _centerClickModeMechId;

    /// <summary>プロファイル既定中心を、散開マップ上でドラッグ調整するモード。</summary>
    private bool _profileCenterDragMode;

    /// <summary>
    /// 散開ポジ + オブジェクトマーカー + AoE ゾーンをまとめて描画＆ドラッグ編集する
    /// 汎用キャンバス。<paramref name="mechanic"/> が null のときは PT 散開ポジのみ。
    /// 非 null のときはそのメカニクスの ObjectMarkers / AoeZones も同じ地図上に重ねる。
    /// 形状はギミック側の override → プロファイル既定 の順で解決。
    /// </summary>
    private void DrawSpreadPositionMapEditor(StrategyProfile profile, MechanicStrategy? mechanic = null)
    {
        // 寸法解決：メカニクス → フェーズ既定 → プロファイル既定
        var phaseSpec = mechanic is not null
            ? StrategyPlanResolver.GetPhaseSpec(profile, mechanic.Phase)
            : null;
        var shape = mechanic?.ArenaShape ?? phaseSpec?.Shape ?? profile.ArenaShape ?? "circle";
        var radius = mechanic?.ArenaRadius ?? phaseSpec?.Radius ?? profile.ArenaRadius ?? 20.0;
        var width = mechanic?.ArenaWidth ?? phaseSpec?.Width ?? profile.ArenaWidth ?? radius * 2;
        var depth = mechanic?.ArenaDepth ?? phaseSpec?.Depth ?? profile.ArenaDepth ?? radius * 2;

        var halfW = string.Equals(shape, "circle", StringComparison.OrdinalIgnoreCase)
            ? (float)radius
            : (float)(width * 0.5);
        var halfD = string.Equals(shape, "circle", StringComparison.OrdinalIgnoreCase)
            ? halfW
            : (float)(depth * 0.5);
        if (halfW <= 0) halfW = 20f;
        if (halfD <= 0) halfD = 20f;

        var canvasSize = 320f * ImGuiHelpers.GlobalScale;
        var pos = ImGui.GetCursorScreenPos();
        var center = new Vector2(pos.X + canvasSize * 0.5f, pos.Y + canvasSize * 0.5f);
        var mapHalf = canvasSize * 0.5f - 8f * ImGuiHelpers.GlobalScale;
        // ピクセル → メートル換算は X/Z 軸独立に持つ
        var pxPerMeterX = mapHalf / halfW;
        var pxPerMeterZ = mapHalf / halfD;
        var draw = ImGui.GetWindowDrawList();

        // 背景：形状に応じた塗りと境界
        if (string.Equals(shape, "circle", StringComparison.OrdinalIgnoreCase))
        {
            draw.AddCircleFilled(center, mapHalf, 0xC0181C25, 64);
            draw.AddCircle(center, mapHalf, 0x80FFFFFF, 64, 1.2f);
        }
        else
        {
            var rectMin = new Vector2(center.X - halfW * pxPerMeterX, center.Y - halfD * pxPerMeterZ);
            var rectMax = new Vector2(center.X + halfW * pxPerMeterX, center.Y + halfD * pxPerMeterZ);
            draw.AddRectFilled(rectMin, rectMax, 0xC0181C25);
            draw.AddRect(rectMin, rectMax, 0x80FFFFFF, 0f, ImDrawFlags.None, 1.2f);
        }
        // 5m ごとの薄いグリッド
        var gridStep = 5f;
        for (var m = gridStep; m < Math.Max(halfW, halfD); m += gridStep)
        {
            var rxPx = pxPerMeterX * m;
            var rzPx = pxPerMeterZ * m;
            if (string.Equals(shape, "circle", StringComparison.OrdinalIgnoreCase))
            {
                if (m < halfW) draw.AddCircle(center, rxPx, 0x40FFFFFF, 48, 0.8f);
            }
            else
            {
                // 縦線（東西方向の刻み）
                if (m < halfW)
                {
                    draw.AddLine(new Vector2(center.X + rxPx, center.Y - halfD * pxPerMeterZ),
                                  new Vector2(center.X + rxPx, center.Y + halfD * pxPerMeterZ), 0x30FFFFFF, 0.7f);
                    draw.AddLine(new Vector2(center.X - rxPx, center.Y - halfD * pxPerMeterZ),
                                  new Vector2(center.X - rxPx, center.Y + halfD * pxPerMeterZ), 0x30FFFFFF, 0.7f);
                }
                // 横線（南北方向の刻み）
                if (m < halfD)
                {
                    draw.AddLine(new Vector2(center.X - halfW * pxPerMeterX, center.Y + rzPx),
                                  new Vector2(center.X + halfW * pxPerMeterX, center.Y + rzPx), 0x30FFFFFF, 0.7f);
                    draw.AddLine(new Vector2(center.X - halfW * pxPerMeterX, center.Y - rzPx),
                                  new Vector2(center.X + halfW * pxPerMeterX, center.Y - rzPx), 0x30FFFFFF, 0.7f);
                }
            }
        }
        // 中央十字
        draw.AddLine(new Vector2(center.X, center.Y - mapHalf),
                     new Vector2(center.X, center.Y + mapHalf), 0x40FFFFFF, 0.8f);
        draw.AddLine(new Vector2(center.X - mapHalf, center.Y),
                     new Vector2(center.X + mapHalf, center.Y), 0x40FFFFFF, 0.8f);
        // 方位ラベル
        AddCenteredMapText(draw, new Vector2(center.X, pos.Y + 4f), "N", 0xFFCCCCCC);
        AddCenteredMapText(draw, new Vector2(center.X, pos.Y + canvasSize - 16f), "S", 0xFFCCCCCC);
        AddCenteredMapText(draw, new Vector2(pos.X + 8f, center.Y - 7f), "W", 0xFFCCCCCC);
        AddCenteredMapText(draw, new Vector2(pos.X + canvasSize - 16f, center.Y - 7f), "E", 0xFFCCCCCC);

        // 距離ラベル（ルーラー）：5m 刻みで「5」「10」「15」を東軸と南軸の交差近くに描画。
        // 「マップの大きさ何メートル？」の視覚指標。
        for (var m = (int)gridStep; m <= (int)Math.Min(halfW, halfD); m += (int)gridStep)
        {
            var rxPx = pxPerMeterX * m;
            var rzPx = pxPerMeterZ * m;
            // 東軸ラベル（半径 m メートルの位置）
            AddCenteredMapText(draw, new Vector2(center.X + rxPx, center.Y + 8f), $"{m}", 0x80B0B0B0u);
            // 南軸ラベル
            AddCenteredMapText(draw, new Vector2(center.X + 12f, center.Y + rzPx - 4f), $"{m}", 0x80B0B0B0u);
        }
        // 全体寸法表示（左上）
        var dimText = string.Equals(shape, "circle", StringComparison.OrdinalIgnoreCase)
            ? $"⌀{halfW * 2:0}m"
            : $"{halfW * 2:0}×{halfD * 2:0}m";
        AddCenteredMapText(draw, new Vector2(pos.X + 28f, pos.Y + 12f), dimText, 0xFFFFD080u);

        // フィールドマーカー（A/B/C/D/1-4）をライブ表示。
        // ユーザーが当日マーカーを置いた瞬間に校正基準として見える。
        DrawWaymarkOverlay(draw, profile, mechanic, center, pxPerMeterX, pxPerMeterZ, halfW, halfD, mapHalf);
        DrawLocalPlayerOverlay(draw, profile, mechanic, center, pxPerMeterX, pxPerMeterZ, halfW, halfD);

        // 重要：ImGui の hit-test は宣言順。BG ボタンを先に置くと dots が hover を取れない。
        // よって dots を **先に** 宣言、BG ボタンは **最後に** 置いて空白部分のクリックだけ拾う。
        var anyDotActive = false;
        for (var i = 0; i < profile.SpreadPositions.Count; i++)
        {
            var sp = profile.SpreadPositions[i];
            var x = (float)sp.X;
            var z = (float)sp.Z;
            var px = center.X + x * pxPerMeterX;
            var py = center.Y + z * pxPerMeterZ;
            var dotR = 12f * ImGuiHelpers.GlobalScale;

            // ドラッグ用の透明ボタンを各 dot の上に重ねる（先に宣言）
            ImGui.SetCursorScreenPos(new Vector2(px - dotR, py - dotR));
            ImGui.InvisibleButton($"##spread-dot-{i}", new Vector2(dotR * 2, dotR * 2));
            var hovered = ImGui.IsItemHovered();
            var active = ImGui.IsItemActive();
            if (active) anyDotActive = true;

            if (active && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0.0f))
            {
                var delta = ImGui.GetIO().MouseDelta;
                if (delta.X != 0 || delta.Y != 0)
                {
                    sp.X += delta.X / pxPerMeterX;
                    sp.Z += delta.Y / pxPerMeterZ;
                    _dirty = true;
                }
            }

            // ドット本体（描画は座標確定後）
            var fill = ParseHex(sp.Color, 0xFFF472B6);
            var ringColor = active ? 0xFFFFFFFF : (hovered ? 0xFFCCCCCC : 0xFF888888u);
            // ドラッグ後の最新座標で再計算
            var sx = (float)sp.X;
            var sz = (float)sp.Z;
            var spx = center.X + sx * pxPerMeterX;
            var spy = center.Y + sz * pxPerMeterZ;
            draw.AddCircleFilled(new Vector2(spx, spy), dotR, fill, 24);
            draw.AddCircle(new Vector2(spx, spy), dotR, ringColor, 24, hovered ? 2.5f : 1.5f);
            var label = sp.Label ?? sp.Slot;
            AddCenteredMapText(draw, new Vector2(spx, spy - 4f), label, 0xFF000000);

            if (hovered)
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted($"{sp.Slot} ({sp.Role ?? "—"})");
                ImGui.TextDisabled($"X={sp.X:0.0}m / Z={sp.Z:0.0}m");
                ImGui.TextDisabled("ドラッグで移動");
                ImGui.EndTooltip();
            }
        }

        // === メカニクス専用レイヤ群（ドラッグ可能） ===
        // 宣言順 = ヒット優先順位。PT ドット > オブジェクトマーカー > AoE 中心ハンドル > BG
        if (mechanic is not null)
        {
            if (mechanic.ObjectMarkers.Count > 0)
            {
                DrawObjectMarkersLayer(draw, center, pxPerMeterX, pxPerMeterZ, mechanic);
            }
            if (mechanic.AoeZones.Count > 0)
            {
                DrawAoeZonesLayer(draw, center, pxPerMeterX, pxPerMeterZ, mechanic);
            }
        }

        var profileCenterDragMode = mechanic is null && _profileCenterDragMode;
        var mechanicCenterDragMode = mechanic is not null &&
            string.Equals(_centerClickModeMechId, mechanic.Id, StringComparison.Ordinal);
        var centerDragMode = profileCenterDragMode || mechanicCenterDragMode;
        if (centerDragMode)
        {
            DrawCenterDragGuide(draw, center, mapHalf);
        }

        // dots の後で BG ボタン（ダブルクリック追加 / 中心ドラッグ用）。
        // dots が hover を奪った場所では発火しない。
        ImGui.SetCursorScreenPos(pos);
        ImGui.InvisibleButton("##spread-map-bg", new Vector2(canvasSize, canvasSize));
        var bgActive = ImGui.IsItemActive();
        var bgHovered = ImGui.IsItemHovered();
        if (centerDragMode && !anyDotActive && bgActive && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0.0f))
        {
            var delta = ImGui.GetIO().MouseDelta;
            if (delta.X != 0 || delta.Y != 0)
            {
                if (profileCenterDragMode)
                {
                    ArenaCenterDragPolicy.ApplyProfileCenterDelta(
                        profile,
                        delta.X / pxPerMeterX,
                        delta.Y / pxPerMeterZ);
                }
                else
                {
                    ArenaCenterDragPolicy.ApplyMechanicCenterDelta(
                        profile,
                        mechanic!,
                        delta.X / pxPerMeterX,
                        delta.Y / pxPerMeterZ);
                }
                _dirty = true;
            }
        }
        if (!anyDotActive && bgHovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            var mouse = ImGui.GetMousePos();
            var dx = mouse.X - center.X;
            var dy = mouse.Y - center.Y;
            var relX = dx / pxPerMeterX; // キャンバス上での相対 X（メートル）
            var relZ = dy / pxPerMeterZ; // 同 Z

            if (centerDragMode)
            {
                if (profileCenterDragMode)
                {
                    ArenaCenterDragPolicy.ApplyProfileCenterDelta(profile, relX, relZ);
                }
                else
                {
                    ArenaCenterDragPolicy.ApplyMechanicCenterDelta(profile, mechanic!, relX, relZ);
                }
                _dirty = true;
            }
            else
            {
                profile.SpreadPositions.Add(new StrategyPosition
                {
                    Slot = $"P{profile.SpreadPositions.Count + 1}",
                    Label = $"P{profile.SpreadPositions.Count + 1}",
                    X = relX,
                    Z = relZ,
                    Color = "#F472B6",
                });
                _dirty = true;
            }
        }

        // 次のウィジェット用にカーソルをキャンバスの下に
        ImGui.SetCursorScreenPos(new Vector2(pos.X, pos.Y + canvasSize));
        ImGui.Dummy(new Vector2(canvasSize, 0f));
    }

    private static void DrawCenterDragGuide(ImDrawListPtr draw, Vector2 center, float mapHalf)
    {
        const uint GuideColor = 0xFF24BFFBu;
        const uint GuideShadow = 0xC0000000u;
        draw.AddCircle(center, 10f * ImGuiHelpers.GlobalScale, GuideShadow, 24, 3.5f);
        draw.AddCircle(center, 10f * ImGuiHelpers.GlobalScale, GuideColor, 24, 2f);
        draw.AddLine(
            new Vector2(center.X - mapHalf, center.Y),
            new Vector2(center.X + mapHalf, center.Y),
            0x50FFFFFFu,
            1f);
        draw.AddLine(
            new Vector2(center.X, center.Y - mapHalf),
            new Vector2(center.X, center.Y + mapHalf),
            0x50FFFFFFu,
            1f);
        AddCenteredMapText(draw, new Vector2(center.X, center.Y - 22f * ImGuiHelpers.GlobalScale), "CENTER", GuideColor);
    }

    /// <summary>
    /// 当日 PT が設置したフィールドマーカー（A〜D / 1〜4）をキャンバスに重ねて描画。
    /// 設置されていないものはスキップ。アリーナ中心からの相対位置で描画する。
    /// </summary>
    /// <remarks>
    /// 校正用。「A をボス位置に置いて → A の表示が中央に来るように中心位置を決める」
    /// のような使い方をユーザーがしやすい。
    /// </remarks>
    private void DrawWaymarkOverlay(
        ImDrawListPtr draw,
        StrategyProfile profile,
        MechanicStrategy? mechanic,
        Vector2 center,
        float pxPerMeterX,
        float pxPerMeterZ,
        float halfW,
        float halfD,
        float mapHalf)
    {
        // アリーナ中心の世界座標を解決：mechanic > phase > profile（何もなければ 0,0）
        var phaseSpec = mechanic is not null
            ? StrategyPlanResolver.GetPhaseSpec(profile, mechanic.Phase)
            : null;
        var arenaCx = mechanic?.ArenaCenterX ?? phaseSpec?.CenterX ?? profile.ArenaCenterX ?? 0.0;
        var arenaCz = mechanic?.ArenaCenterZ ?? phaseSpec?.CenterZ ?? profile.ArenaCenterZ ?? 0.0;

        // FFXIV のウェイマーク（フィールドマーカー）8 種：A〜D と 1〜4。
        // 文字／数字が同色ペア（A=1=赤, B=2=黄, C=3=青, D=4=紫）— 実ゲームの色味に合わせる。
        // 注意：ImGui の色 uint は ABGR 順（バイト下位から R→G→B→A）。
        // 例：赤 #E74C3C は { R=0xE7, G=0x4C, B=0x3C } → 0xFF_3C_4C_E7。
        var letters = new[] { "A", "B", "C", "D", "1", "2", "3", "4" };
        var colors = new uint[]
        {
            0xFF3C4CE7u, // A：赤 (#E74C3C)
            0xFF0FC4F1u, // B：黄 (#F1C40F)
            0xFFDB9834u, // C：青 (#3498DB)
            0xFFB6599Bu, // D：紫 (#9B59B6)
            0xFF3C4CE7u, // 1：赤
            0xFF0FC4F1u, // 2：黄
            0xFFDB9834u, // 3：青
            0xFFB6599Bu, // 4：紫
        };
        for (var i = 0; i < letters.Length; i++)
        {
            var letter = letters[i];
            var live = FfxivEchoes.Capture.WaymarkProvider.TryGetPosition(letter);
            if (live is null) continue;
            var dx = (float)(live.Value.X - arenaCx);
            var dz = (float)(live.Value.Z - arenaCz);
            // キャンバス外に出るマーカーは見切れ位置にクランプして「方向だけは分かる」状態に
            var maxX = halfW * 1.05f;
            var maxZ = halfD * 1.05f;
            var clampedX = Math.Clamp(dx, -maxX, maxX);
            var clampedZ = Math.Clamp(dz, -maxZ, maxZ);
            var clipped = clampedX != dx || clampedZ != dz;
            var px = center.X + clampedX * pxPerMeterX;
            var py = center.Y + clampedZ * pxPerMeterZ;
            var dotR = 9f * ImGuiHelpers.GlobalScale;
            // 二重リング（ライブ感を出すために少し大きめのアウトライン）
            draw.AddCircleFilled(new Vector2(px, py), dotR, colors[i] & 0xC0FFFFFFu, 24);
            draw.AddCircle(new Vector2(px, py), dotR, colors[i] | 0xFF000000u,
                clipped ? 24 : 24, clipped ? 1.0f : 2.0f);
            AddCenteredMapText(draw, new Vector2(px, py - 5f), letter, 0xFF000000u);
        }
    }

    private void DrawLocalPlayerOverlay(
        ImDrawListPtr draw,
        StrategyProfile profile,
        MechanicStrategy? mechanic,
        Vector2 center,
        float pxPerMeterX,
        float pxPerMeterZ,
        float halfW,
        float halfD)
    {
        if (_objectTable?.LocalPlayer is not { } self)
        {
            return;
        }

        var phaseSpec = mechanic is not null
            ? StrategyPlanResolver.GetPhaseSpec(profile, mechanic.Phase)
            : null;
        var arenaCx = mechanic?.ArenaCenterX ?? phaseSpec?.CenterX ?? profile.ArenaCenterX ?? 0.0;
        var arenaCz = mechanic?.ArenaCenterZ ?? phaseSpec?.CenterZ ?? profile.ArenaCenterZ ?? 0.0;
        var dx = (float)(self.Position.X - arenaCx);
        var dz = (float)(self.Position.Z - arenaCz);
        var maxX = halfW * 1.05f;
        var maxZ = halfD * 1.05f;
        var clampedX = Math.Clamp(dx, -maxX, maxX);
        var clampedZ = Math.Clamp(dz, -maxZ, maxZ);
        var clipped = clampedX != dx || clampedZ != dz;
        var px = center.X + clampedX * pxPerMeterX;
        var py = center.Y + clampedZ * pxPerMeterZ;
        var dotR = 8f * ImGuiHelpers.GlobalScale;

        draw.AddCircleFilled(new Vector2(px, py), dotR, 0xFF7DD3FCu, 24);
        draw.AddCircle(new Vector2(px, py), dotR + 2f, clipped ? 0xFFFFA64Du : 0xFFFFFFFFu, 24, 2f);
        AddCenteredMapText(draw, new Vector2(px, py - 4f), "YOU", 0xFF000000u);
    }

    /// <summary>
    /// メカニクスの ObjectMarkers をキャンバスに描画＆ドラッグハンドル提供。
    /// 形状ごとに色／シンボルを変える。中心ハンドルでドラッグ移動。
    /// </summary>
    private void DrawObjectMarkersLayer(ImDrawListPtr draw, Vector2 center, float pxPerMeterX, float pxPerMeterZ, MechanicStrategy mechanic)
    {
        var dotR = 10f * ImGuiHelpers.GlobalScale;
        for (var i = 0; i < mechanic.ObjectMarkers.Count; i++)
        {
            var mk = mechanic.ObjectMarkers[i];
            var px = center.X + (float)mk.X * pxPerMeterX;
            var py = center.Y + (float)mk.Z * pxPerMeterZ;

            ImGui.SetCursorScreenPos(new Vector2(px - dotR, py - dotR));
            ImGui.InvisibleButton($"##obj-{mechanic.Id}-{i}", new Vector2(dotR * 2, dotR * 2));
            var hovered = ImGui.IsItemHovered();
            var active = ImGui.IsItemActive();
            if (active && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0.0f))
            {
                var d = ImGui.GetIO().MouseDelta;
                if (d.X != 0 || d.Y != 0)
                {
                    mk.X += d.X / pxPerMeterX;
                    mk.Z += d.Y / pxPerMeterZ;
                    _dirty = true;
                }
            }

            var fill = ParseHex(mk.Color, 0xFFCCCCCCu);
            var ring = active ? 0xFFFFFFFFu : (hovered ? 0xFFCCCCCCu : 0xFF888888u);
            var pp = new Vector2(center.X + (float)mk.X * pxPerMeterX,
                                  center.Y + (float)mk.Z * pxPerMeterZ);
            switch ((mk.Shape ?? "circle").ToLowerInvariant())
            {
                case "square":
                    draw.AddRectFilled(pp - new Vector2(dotR, dotR), pp + new Vector2(dotR, dotR), fill);
                    draw.AddRect(pp - new Vector2(dotR, dotR), pp + new Vector2(dotR, dotR), ring, 0f, ImDrawFlags.None, 1.5f);
                    break;
                case "triangle":
                    draw.AddTriangleFilled(
                        pp + new Vector2(0, -dotR),
                        pp + new Vector2(dotR, dotR * 0.8f),
                        pp + new Vector2(-dotR, dotR * 0.8f), fill);
                    draw.AddTriangle(
                        pp + new Vector2(0, -dotR),
                        pp + new Vector2(dotR, dotR * 0.8f),
                        pp + new Vector2(-dotR, dotR * 0.8f), ring, 1.5f);
                    break;
                case "diamond":
                    draw.AddQuadFilled(
                        pp + new Vector2(0, -dotR), pp + new Vector2(dotR, 0),
                        pp + new Vector2(0, dotR), pp + new Vector2(-dotR, 0), fill);
                    draw.AddQuad(
                        pp + new Vector2(0, -dotR), pp + new Vector2(dotR, 0),
                        pp + new Vector2(0, dotR), pp + new Vector2(-dotR, 0), ring, 1.5f);
                    break;
                default:
                    draw.AddCircleFilled(pp, dotR, fill, 18);
                    draw.AddCircle(pp, dotR, ring, 18, 1.5f);
                    break;
            }

            if (!string.IsNullOrEmpty(mk.Label))
            {
                AddCenteredMapText(draw, pp + new Vector2(0, -dotR - 6f), mk.Label!, 0xFFFFFFFFu);
            }
            if (hovered)
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(string.IsNullOrEmpty(mk.Label) ? mk.Id : mk.Label);
                ImGui.TextDisabled($"X={mk.X:0.0}m / Z={mk.Z:0.0}m / shape={mk.Shape ?? "circle"}");
                ImGui.TextDisabled("ドラッグで移動");
                ImGui.EndTooltip();
            }
        }
    }

    /// <summary>
    /// メカニクスの AoE ゾーンを描画＆中心ドラッグハンドル提供。
    /// 半径や回転は下のプロパティ表で編集する想定。
    /// </summary>
    private void DrawAoeZonesLayer(ImDrawListPtr draw, Vector2 center, float pxPerMeterX, float pxPerMeterZ, MechanicStrategy mechanic)
    {
        var handleR = 6f * ImGuiHelpers.GlobalScale;
        for (var i = 0; i < mechanic.AoeZones.Count; i++)
        {
            var z = mechanic.AoeZones[i];
            var ox = center.X + (float)z.X * pxPerMeterX;
            var oy = center.Y + (float)z.Z * pxPerMeterZ;
            var origin = new Vector2(ox, oy);
            var pixelR = (float)z.RadiusM * pxPerMeterX;
            if (pixelR < 4f) pixelR = 4f;

            uint fill, stroke;
            if (z.IsDanger)
            {
                fill = 0x556B6BF6u;   // 半透明赤
                stroke = 0xFF6B6BF6u;
            }
            else
            {
                fill = 0x5577C534u;   // 半透明緑
                stroke = 0xFF7BD391u;
            }
            if (!string.IsNullOrEmpty(z.Color))
            {
                stroke = ParseHex(z.Color, stroke);
                fill = (stroke & 0x00FFFFFFu) | 0x55000000u;
            }

            switch ((z.Shape ?? "circle").ToLowerInvariant())
            {
                case "circle":
                    draw.AddCircleFilled(origin, pixelR, fill, 48);
                    draw.AddCircle(origin, pixelR, stroke, 48, 1.5f);
                    break;
                case "donut":
                {
                    var inner = (z.InnerRadiusM ?? z.RadiusM * 0.5);
                    var innerPx = (float)inner * pxPerMeterX;
                    if (innerPx < 2f) innerPx = 2f;
                    const int segs = 48;
                    for (var s = 0; s < segs; s++)
                    {
                        var a1 = (float)(s * Math.PI * 2 / segs);
                        var a2 = (float)((s + 1) * Math.PI * 2 / segs);
                        var po1 = new Vector2(origin.X + MathF.Cos(a1) * pixelR, origin.Y + MathF.Sin(a1) * pixelR);
                        var po2 = new Vector2(origin.X + MathF.Cos(a2) * pixelR, origin.Y + MathF.Sin(a2) * pixelR);
                        var pi1 = new Vector2(origin.X + MathF.Cos(a1) * innerPx, origin.Y + MathF.Sin(a1) * innerPx);
                        var pi2 = new Vector2(origin.X + MathF.Cos(a2) * innerPx, origin.Y + MathF.Sin(a2) * innerPx);
                        draw.AddQuadFilled(po1, po2, pi2, pi1, fill);
                    }
                    draw.AddCircle(origin, pixelR, stroke, segs, 1.5f);
                    draw.AddCircle(origin, innerPx, stroke, segs, 1.5f);
                    break;
                }
                case "cone":
                {
                    var rotRad = (float)((z.RotationDeg ?? -90.0) * Math.PI / 180.0);
                    var halfFan = (float)((z.FanDeg ?? 90.0) * Math.PI / 360.0);
                    const int segs = 24;
                    var path = new List<Vector2> { origin };
                    for (var s = 0; s <= segs; s++)
                    {
                        var t = (float)s / segs;
                        var a = rotRad - halfFan + (halfFan * 2f) * t;
                        path.Add(new Vector2(origin.X + MathF.Cos(a) * pixelR, origin.Y + MathF.Sin(a) * pixelR));
                    }
                    foreach (var p in path) draw.PathLineTo(p);
                    draw.PathFillConvex(fill);
                    break;
                }
                case "rect":
                {
                    var rotRad = (float)((z.RotationDeg ?? 0.0) * Math.PI / 180.0);
                    var halfWPx = (float)((z.HalfWidthM ?? 2.0) * pxPerMeterX);
                    if (halfWPx < 3f) halfWPx = 3f;
                    var fwd = new Vector2(MathF.Cos(rotRad), MathF.Sin(rotRad));
                    var perp = new Vector2(-fwd.Y, fwd.X);
                    var p1 = origin - perp * halfWPx;
                    var p2 = origin + perp * halfWPx;
                    var p3 = p2 + fwd * pixelR;
                    var p4 = p1 + fwd * pixelR;
                    draw.AddQuadFilled(p1, p2, p3, p4, fill);
                    draw.AddQuad(p1, p2, p3, p4, stroke, 1.5f);
                    break;
                }
            }

            // 形状全体をドラッグハンドルにする：中心の小さい点だけだと
            // ピクセル精度で当てる必要があり辛い。形状の bounding box
            // （半径ぶんの正方形）を当たり判定にして、図形のどこを掴んでも動く。
            // ※ PT ドット / オブジェクトマーカーは先に宣言済なので、それらの上では
            //   そちらが優先（ヒット順位）。AoE は最後尾。
            var bboxR = MathF.Max(pixelR, handleR);
            ImGui.SetCursorScreenPos(new Vector2(ox - bboxR, oy - bboxR));
            ImGui.InvisibleButton($"##aoe-{mechanic.Id}-{i}", new Vector2(bboxR * 2, bboxR * 2));
            var hovered = ImGui.IsItemHovered();
            var active = ImGui.IsItemActive();
            if (active && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0.0f))
            {
                var d = ImGui.GetIO().MouseDelta;
                if (d.X != 0 || d.Y != 0)
                {
                    z.X += d.X / pxPerMeterX;
                    z.Z += d.Y / pxPerMeterZ;
                    _dirty = true;
                }
            }
            // 中心マーカー（位置の目安として残す）。ドラッグ判定は bbox 全体。
            draw.AddCircleFilled(origin, handleR, hovered ? 0xFFFFFFFFu : 0xFFCCCCCCu, 12);
            draw.AddCircle(origin, handleR, 0xFF000000u, 12, 1f);
            // hover 中は形状外周も白く強調して「これがいま選択されている」と分かるように
            if (hovered)
            {
                draw.AddCircle(origin, pixelR + 1f, 0xFFFFFFFFu, 48, 2f);
            }
            if (!string.IsNullOrEmpty(z.Label))
            {
                AddCenteredMapText(draw, origin + new Vector2(0, -handleR - 6f), z.Label!, 0xFFFFFFFFu);
            }
        }
    }

    private static void AddCenteredMapText(ImDrawListPtr draw, Vector2 center, string text, uint color)
    {
        var size = ImGui.CalcTextSize(text);
        draw.AddText(new Vector2(center.X - size.X * 0.5f, center.Y - size.Y * 0.5f), color, text);
    }

    private static uint ParseHex(string? hex, uint fallback)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length != 7 || hex[0] != '#') return fallback;
        if (!uint.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var rgb)) return fallback;
        var r = (rgb >> 16) & 0xFF;
        var g = (rgb >> 8) & 0xFF;
        var b = rgb & 0xFF;
        // ABGR for ImGui
        return (0xFFu << 24) | (b << 16) | (g << 8) | r;
    }

    /// <summary>
    /// 標準 8 方向散開ポジを生成。アリーナの半径（halfW / halfD）に追従するため
    /// どんな広さの戦場でもはみ出さない。Tank/Healer は半径の 70%、DPS は 50% に配置。
    /// </summary>
    /// <remarks>
    /// 旧実装は X=±14, Z=±14, ±10 を固定で返していたため、20m × 21m などの狭い
    /// アリーナでは H2 等が範囲外に飛び出していた。
    /// </remarks>
    private static List<StrategyPosition> CreateEightWaySpreadPositions(double halfW = 20.0, double halfD = 20.0)
    {
        var hw = halfW > 0 ? halfW : 20.0;
        var hd = halfD > 0 ? halfD : 20.0;
        const double thRatio = 0.70; // Tank / Healer：外周寄り
        const double dpRatio = 0.50; // DPS：内側寄り
        return new List<StrategyPosition>
        {
            new() { Slot = "MT", Label = "MT", Role = "tank",   X = 0,           Z = -hd * thRatio, Color = "#60A5FA" },
            new() { Slot = "ST", Label = "ST", Role = "tank",   X = 0,           Z =  hd * thRatio, Color = "#60A5FA" },
            new() { Slot = "H1", Label = "H1", Role = "healer", X = -hw * thRatio, Z = 0,           Color = "#34D399" },
            new() { Slot = "H2", Label = "H2", Role = "healer", X =  hw * thRatio, Z = 0,           Color = "#34D399" },
            new() { Slot = "D1", Label = "D1", Role = "dps",    X = -hw * dpRatio, Z = -hd * dpRatio, Color = "#F87171" },
            new() { Slot = "D2", Label = "D2", Role = "dps",    X =  hw * dpRatio, Z = -hd * dpRatio, Color = "#F87171" },
            new() { Slot = "D3", Label = "D3", Role = "dps",    X = -hw * dpRatio, Z =  hd * dpRatio, Color = "#FBBF24" },
            new() { Slot = "D4", Label = "D4", Role = "dps",    X =  hw * dpRatio, Z =  hd * dpRatio, Color = "#FBBF24" },
        };
    }

    /// <summary>
    /// プロファイル / フェーズ / メカニクスの寸法解決チェイン。半幅 (X方向) と半奥行 (Z方向) を返す。
    /// 解決順：mechanic override → phase preset → profile default → 20.0。
    /// </summary>
    private static (double halfW, double halfD) ResolveArenaHalfExtents(StrategyProfile profile, MechanicStrategy? mechanic = null)
    {
        var phaseSpec = mechanic is not null
            ? StrategyPlanResolver.GetPhaseSpec(profile, mechanic.Phase)
            : null;
        var shape = mechanic?.ArenaShape ?? phaseSpec?.Shape ?? profile.ArenaShape ?? "circle";
        var radius = mechanic?.ArenaRadius ?? phaseSpec?.Radius ?? profile.ArenaRadius ?? 20.0;
        var width = mechanic?.ArenaWidth ?? phaseSpec?.Width ?? profile.ArenaWidth ?? radius * 2;
        var depth = mechanic?.ArenaDepth ?? phaseSpec?.Depth ?? profile.ArenaDepth ?? radius * 2;
        var isCircle = string.Equals(shape, "circle", StringComparison.OrdinalIgnoreCase);
        var halfW = isCircle ? radius : width * 0.5;
        var halfD = isCircle ? radius : depth * 0.5;
        if (halfW <= 0) halfW = 20.0;
        if (halfD <= 0) halfD = 20.0;
        return (halfW, halfD);
    }

    private void DrawHeader(string zone)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"ゾーン: ");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(1f, 0.85f, 0.4f, 1f), zone);
        ImGui.SameLine();
        ImGui.TextDisabled($"  /  バージョン {_workingCopy!.Version}  /  トリガー {_workingCopy.Triggers.Count} 件");

        ImGui.SameLine(0, 32f * ImGuiHelpers.GlobalScale);
        if (_externalStoreChangedWhileDirty)
        {
            ImGui.TextColored(new Vector4(1f, 0.7f, 0.35f, 1f),
                "外部でこのゾーンが更新/削除されました。");
            ImGui.SameLine();
            if (ImGui.SmallButton("最新状態を読み込む"))
            {
                LoadWorkingCopy(zone);
                return;
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("未保存の編集内容を捨てて、現在のファイル状態を読み直します。");
            }
            ImGui.SameLine(0, 16f * ImGuiHelpers.GlobalScale);
        }
        var saveLabel = _externalStoreChangedWhileDirty
            ? "この内容で上書き保存"
            : (_dirty ? "保存（未保存の変更あり）" : "保存");
        if (ImGui.Button(saveLabel))
        {
            try
            {
                _triggerStore.SaveZone(zone, _workingCopy);
                LoadWorkingCopy(zone);
            }
            catch (Exception ex)
            {
                ImGui.OpenPopup("save-error");
                ImGui.SetNextWindowSize(new Vector2(400, 0));
                _saveError = ex.Message;
            }
        }

        if (ImGui.BeginPopupModal("save-error", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped(_saveError ?? "保存に失敗しました。");
            if (ImGui.Button("OK"))
            {
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("変更を破棄"))
        {
            LoadWorkingCopy(zone);
        }
    }

    private string? _saveError;

    private void DrawTriggerListPanel()
    {
        if (_workingCopy is null)
        {
            return;
        }

        if (ImGui.Button("新規トリガー"))
        {
            var newTrigger = new TriggerDefinition
            {
                Id = $"new_trigger_{_workingCopy.Triggers.Count + 1}",
                Type = "cast_start",
                Match = new MatchCondition(),
                Actions = new List<ActionDefinition> { new() { Type = "tts", Text = "" } },
            };
            _workingCopy.Triggers.Add(newTrigger);
            _editingTriggerId = newTrigger.Id;
            _dirty = true;
        }
        ImGui.SameLine(0, 24f * ImGuiHelpers.GlobalScale);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.55f, 0.3f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.25f, 0.7f, 0.4f, 1f));
        if (ImGui.Button("✨ 録画から自動生成"))
        {
            BeginAutoGeneration();
        }
        ImGui.PopStyleColor(2);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("録画されたボスのキャストから、Lumina の AoE データを参照して\n" +
                             "「TTS + 視覚通知」を含むトリガーを一括自動生成します。\n" +
                             "既に存在する cast_id はスキップ。生成後に個別編集も可能。");
        }

        // 旧バグで auto_cast_XXXX / _source / _source_2 のように重複した
        // トリガーが大量に残っているケース向けクリーンアップボタン。
        ImGui.SameLine(0, 12f * ImGuiHelpers.GlobalScale);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.55f, 0.30f, 0.30f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.70f, 0.40f, 0.40f, 1f));
        if (ImGui.Button("🧹 重複トリガーを掃除"))
        {
            var removed = CleanupDuplicateAutoTriggers();
            _lastCleanupMessage = removed > 0
                ? $"重複 auto_cast_* を {removed} 件削除しました（保存ボタンで永続化）"
                : "重複は見つかりませんでした";
        }
        ImGui.PopStyleColor(2);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "auto_cast_XXXX / auto_cast_XXXX_source / auto_cast_XXXX_source_2 のように\n" +
                "同じ cast_id を指す自動生成トリガーが複数ある場合、\n" +
                "観測回数が一番多いものを 1 つだけ残して残りを削除します。\n" +
                "（手動で作成したトリガーや cast_id が無いものは触りません）");
        }
        if (!string.IsNullOrEmpty(_lastCleanupMessage))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(_lastCleanupMessage);
        }

        DrawAutoGenPreviewPopup();
        ImGui.Spacing();

        if (_workingCopy.Triggers.Count == 0)
        {
            ImGui.TextDisabled("トリガーがありません。「新規トリガー」で追加してください。");
            return;
        }

        // 二段組：左にリスト、右に編集パネル
        var available = ImGui.GetContentRegionAvail();
        var listWidth = MathF.Min(280f * ImGuiHelpers.GlobalScale, available.X * 0.45f);

        if (ImGui.BeginChild("##trigger-list", new Vector2(listWidth, 0), true))
        {
            for (int i = 0; i < _workingCopy.Triggers.Count; i++)
            {
                var t = _workingCopy.Triggers[i];
                var label = $"{(t.Enabled ? "● " : "○ ")}{t.Id}##list-{i}";
                if (ImGui.Selectable(label, _editingTriggerId == t.Id))
                {
                    _editingTriggerId = t.Id;
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"type: {t.Type}\nactions: {t.Actions.Count}\n{(t.Name ?? "(no name)")}");
                }
            }
        }
        ImGui.EndChild();

        ImGui.SameLine();

        if (ImGui.BeginChild("##trigger-edit", new Vector2(0, 0), true))
        {
            DrawTriggerEditor();
        }
        ImGui.EndChild();
    }

    private void DrawTriggerEditor()
    {
        if (_workingCopy is null)
        {
            return;
        }
        if (_editingTriggerId is null)
        {
            ImGui.TextDisabled("左でトリガーを選択してください。");
            return;
        }
        var trigger = _workingCopy.Triggers.FirstOrDefault(t => t.Id == _editingTriggerId);
        if (trigger is null)
        {
            ImGui.TextDisabled("選択されたトリガーが見つかりません。");
            return;
        }

        // 基本情報
        var enabled = trigger.Enabled;
        if (ImGui.Checkbox("有効", ref enabled))
        {
            trigger.Enabled = enabled;
            _dirty = true;
        }

        ImGui.Spacing();
        var id = trigger.Id;
        ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("ID##trigger-id"u8, ref id, 64))
        {
            trigger.Id = id;
            _editingTriggerId = id;
            _dirty = true;
        }

        var name = trigger.Name ?? string.Empty;
        ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("表示名##trigger-name"u8, ref name, 128))
        {
            trigger.Name = string.IsNullOrEmpty(name) ? null : name;
            _dirty = true;
        }

        var typeIndex = Array.IndexOf(EventTypes, trigger.Type);
        if (typeIndex < 0)
        {
            typeIndex = 0;
        }
        var eventTypeLabels = Localization.LocalizeAll(EventTypes, Localization.EventType);
        ImGui.SetNextItemWidth(320f * ImGuiHelpers.GlobalScale);
        if (ImGui.Combo("発火タイミング##trigger-type", ref typeIndex, eventTypeLabels, eventTypeLabels.Length))
        {
            trigger.Type = EventTypes[typeIndex];
            _dirty = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("どのゲームイベントでこのトリガーを発動するか");

        var cooldown = (float)(trigger.Cooldown ?? 0);
        ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputFloat("クールダウン (s)##trigger-cd", ref cooldown))
        {
            trigger.Cooldown = cooldown <= 0 ? null : cooldown;
            _dirty = true;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("マッチ条件");
        DrawMatchEditor(trigger);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("アクション");
        DrawActionsEditor(trigger);

        ImGui.Spacing();
        ImGui.Separator();
        if (ImGui.Button("このトリガーを削除"))
        {
            _workingCopy.Triggers.Remove(trigger);
            _editingTriggerId = null;
            _dirty = true;
        }
        ImGui.SameLine();
        if (ImGui.Button("テスト発動"u8))
        {
            FirePreview(trigger);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("このトリガーを今すぐ発火させて、アクション動作を確認します。");
        }
    }

    private void FirePreview(TriggerDefinition trigger)
    {
        if (_eventBus is null)
        {
            return;
        }
        // 偽の SourceEvent として CombatStartedEvent を使う
        var fakeSource = new Events.CombatStartedEvent(System.DateTimeOffset.UtcNow);
        _eventBus.Publish(new Events.TriggerFiredEvent(
            Timestamp: System.DateTimeOffset.UtcNow,
            Zone: _workingZone,
            TriggerId: trigger.Id,
            TriggerName: trigger.Name,
            Actions: trigger.Actions,
            SourceEvent: fakeSource));
    }

    private void DrawMatchEditor(TriggerDefinition trigger)
    {
        trigger.Match ??= new MatchCondition();
        var m = trigger.Match;

        var castId = m.CastId ?? string.Empty;
        ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("cast_id (例: 0x9D32)##match-cast-id", ref castId, 32))
        {
            m.CastId = string.IsNullOrEmpty(castId) ? null : castId;
            _dirty = true;
        }

        var castName = m.CastName ?? string.Empty;
        ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("cast_name##match-cast-name", ref castName, 128))
        {
            m.CastName = string.IsNullOrEmpty(castName) ? null : castName;
            _dirty = true;
        }

        var statusId = (int)(m.StatusId ?? 0);
        ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("status_id##match-status-id", ref statusId))
        {
            m.StatusId = statusId <= 0 ? null : (uint)statusId;
            _dirty = true;
        }

        var source = m.Source ?? string.Empty;
        ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("source##match-source", ref source, 64))
        {
            m.Source = string.IsNullOrEmpty(source) ? null : source;
            _dirty = true;
        }

        // target は単純に文字列で（M8 では配列対応はしない）
        var target = m.Target?.Values is { Count: > 0 } v ? v[0] : string.Empty;
        ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("target (self/tank/healer/...)##match-target", ref target, 32))
        {
            m.Target = string.IsNullOrEmpty(target)
                ? null
                : new TargetSpec(new List<string> { target });
            _dirty = true;
        }
    }

    private void DrawActionsEditor(TriggerDefinition trigger)
    {
        for (int i = 0; i < trigger.Actions.Count; i++)
        {
            var action = trigger.Actions[i];
            ImGui.PushID(i);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted($"#{i + 1}");
            ImGui.SameLine();

            var typeIndex = Array.IndexOf(ActionTypes, action.Type);
            if (typeIndex < 0)
            {
                typeIndex = 0;
            }
            var actionTypeLabels = Localization.LocalizeAll(ActionTypes, Localization.ActionType);
            ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
            if (ImGui.Combo("##action-type", ref typeIndex, actionTypeLabels, actionTypeLabels.Length))
            {
                action.Type = ActionTypes[typeIndex];
                _dirty = true;
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("↑") && i > 0)
            {
                (trigger.Actions[i - 1], trigger.Actions[i]) = (trigger.Actions[i], trigger.Actions[i - 1]);
                _dirty = true;
                ImGui.PopID();
                continue;
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("↓") && i < trigger.Actions.Count - 1)
            {
                (trigger.Actions[i + 1], trigger.Actions[i]) = (trigger.Actions[i], trigger.Actions[i + 1]);
                _dirty = true;
                ImGui.PopID();
                continue;
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("削除"))
            {
                trigger.Actions.RemoveAt(i);
                _dirty = true;
                ImGui.PopID();
                continue;
            }

            // type に応じた最低限のフィールド
            ImGui.Indent(20f);
            switch (action.Type)
            {
                case "tts":
                case "chat_echo":
                case "overlay_text":
                {
                    var text = action.Text ?? string.Empty;
                    ImGui.SetNextItemWidth(380f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputText("text##action-text", ref text, 256))
                    {
                        action.Text = text;
                        _dirty = true;
                    }
                    if (action.Type == "overlay_text")
                    {
                        var dur = (float)(action.Duration ?? 5.0);
                        ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
                        if (ImGui.InputFloat("duration (s)##action-duration", ref dur))
                        {
                            action.Duration = dur <= 0 ? null : dur;
                            _dirty = true;
                        }
                    }
                    break;
                }
                case "wav":
                {
                    var file = action.File ?? string.Empty;
                    ImGui.SetNextItemWidth(380f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputText("file##action-file", ref file, 256))
                    {
                        action.File = file;
                        _dirty = true;
                    }
                    break;
                }
                case "timer_bar":
                {
                    var label = action.Label ?? string.Empty;
                    ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputText("label##action-label", ref label, 128))
                    {
                        action.Label = label;
                        _dirty = true;
                    }
                    var dur = (float)(action.Duration ?? 0.0);
                    ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputFloat("duration (s)##action-duration", ref dur))
                    {
                        action.Duration = dur <= 0 ? null : dur;
                        _dirty = true;
                    }
                    break;
                }
                case "arena_view":
                {
                    // gimmick タイプ
                    var gimmick = action.Gimmick ?? "outer_ring";
                    var gIdx = Array.IndexOf(ArenaViewGimmicks, gimmick);
                    if (gIdx < 0) gIdx = 0;
                    var gimmickLabels = Localization.LocalizeAll(ArenaViewGimmicks, Localization.Gimmick);
                    ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
                    if (ImGui.Combo("ギミック種類##action-gimmick", ref gIdx, gimmickLabels, gimmickLabels.Length))
                    {
                        action.Gimmick = ArenaViewGimmicks[gIdx];
                        _dirty = true;
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(GimmickTooltip(action.Gimmick ?? "outer_ring"));
                    // callout
                    var callout = action.Callout ?? string.Empty;
                    ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputText("読み上げ・表示テキスト##action-callout", ref callout, 128))
                    {
                        action.Callout = callout;
                        _dirty = true;
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("ミニマップ下部に表示されるテキスト（例：「中央安置」）");
                    // duration
                    var dur = (float)(action.Duration ?? 5.0);
                    ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputFloat("表示秒数 (s)##action-duration", ref dur))
                    {
                        action.Duration = dur <= 0 ? null : dur;
                        _dirty = true;
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("ミニマップを表示する秒数。キャスト時間 + α が目安（例：5）");
                    // arena_radius
                    var ar = (float)(action.ArenaRadius ?? 20.0);
                    ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputFloat("アリーナ半径 (m)##action-ar", ref ar))
                    {
                        action.ArenaRadius = ar <= 0 ? null : ar;
                        _dirty = true;
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("プレイヤー位置を描画するためのアリーナ実半径（メートル）。極/絶はだいたい 18-25m");
                    // cone のときだけ direction + fan_deg
                    if (action.Gimmick == "cone")
                    {
                        var dir = action.Direction ?? "N";
                        var dIdx = Array.IndexOf(ArenaViewDirections, dir);
                        if (dIdx < 0) dIdx = 0;
                        var dirLabels = Localization.LocalizeAll(ArenaViewDirections, Localization.Direction);
                        ImGui.SetNextItemWidth(160f * ImGuiHelpers.GlobalScale);
                        if (ImGui.Combo("方向##action-dir", ref dIdx, dirLabels, dirLabels.Length))
                        {
                            action.Direction = ArenaViewDirections[dIdx];
                            _dirty = true;
                        }
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip("コーンが向く方位（北を上として）");
                        var fan = (float)(action.FanDeg ?? 90.0);
                        ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
                        if (ImGui.InputFloat("扇形の角度 (°)##action-fan", ref fan))
                        {
                            action.FanDeg = fan <= 0 ? null : fan;
                            _dirty = true;
                        }
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip("90 で 90 度の扇形（45 度ずつ左右に開く）");
                    }
                    DrawSafeZoneSubEditor(action);
                    break;
                }
                case "direction_call":
                {
                    var fmt = action.Format ?? "cardinal_jp";
                    var fIdx = Array.IndexOf(DirectionFormats, fmt);
                    if (fIdx < 0) fIdx = 0;
                    var fmtLabels = Localization.LocalizeAll(DirectionFormats, Localization.DirectionFormat);
                    ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
                    if (ImGui.Combo("読み上げ形式##action-fmt", ref fIdx, fmtLabels, fmtLabels.Length))
                    {
                        action.Format = DirectionFormats[fIdx];
                        _dirty = true;
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("方位の読み上げ方を選択。日本語方位なら「北」、時計なら「12時」など");

                    var ttsOn = action.Tts ?? true;
                    if (ImGui.Checkbox("TTS で読み上げ##dc-tts", ref ttsOn)) { action.Tts = ttsOn; _dirty = true; }
                    ImGui.SameLine();
                    var ovOn = action.Overlay ?? false;
                    if (ImGui.Checkbox("オーバーレイにも表示##dc-ov", ref ovOn)) { action.Overlay = ovOn; _dirty = true; }
                    DrawSafeZoneSubEditor(action);
                    break;
                }
                case "screen_arrow":
                {
                    var fromStr = action.From ?? "self";
                    ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputText("from##action-from", ref fromStr, 64)) { action.From = fromStr; _dirty = true; }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("矢印の起点。\"self\" / \"boss\" / actor 名等");

                    var durSa = (float)(action.Duration ?? 5.0);
                    ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputFloat("duration (s)##action-dur-sa", ref durSa)) { action.Duration = durSa <= 0 ? null : durSa; _dirty = true; }
                    var color = action.Color ?? "#00FF00";
                    ImGui.SetNextItemWidth(140f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputText("color (#RRGGBB)##action-color-sa", ref color, 16)) { action.Color = color; _dirty = true; }
                    DrawSafeZoneSubEditor(action);
                    break;
                }
                case "field_marker":
                {
                    var shape = action.Shape ?? "circle";
                    var sIdx = Array.IndexOf(FieldShapes, shape);
                    if (sIdx < 0) sIdx = 0;
                    var shapeLabels = Localization.LocalizeAll(FieldShapes, Localization.FieldShape);
                    ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
                    if (ImGui.Combo("形状##action-shape", ref sIdx, shapeLabels, shapeLabels.Length))
                    {
                        action.Shape = FieldShapes[sIdx];
                        _dirty = true;
                    }
                    var rad = (float)(action.Radius ?? 3.0);
                    ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputFloat("半径 (m)##action-fm-rad", ref rad)) { action.Radius = rad <= 0 ? null : rad; _dirty = true; }
                    var durFm = (float)(action.Duration ?? 5.0);
                    ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputFloat("表示秒数 (s)##action-dur-fm", ref durFm)) { action.Duration = durFm <= 0 ? null : durFm; _dirty = true; }
                    var colorFm = action.Color ?? "#00FF00";
                    ImGui.SetNextItemWidth(140f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputText("色 (#RRGGBB)##action-color-fm", ref colorFm, 16)) { action.Color = colorFm; _dirty = true; }
                    DrawSafeZoneSubEditor(action);
                    break;
                }
                case "proximity_feedback":
                {
                    var tol = (float)(action.Tolerance ?? 3.0);
                    ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputFloat("tolerance (m)##action-pf-tol", ref tol)) { action.Tolerance = tol <= 0 ? null : tol; _dirty = true; }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("安置に入った/出た判定の半径");

                    var inS = action.InSound ?? string.Empty;
                    ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputText("in_sound##action-pf-in", ref inS, 256)) { action.InSound = inS; _dirty = true; }
                    var outS = action.OutSound ?? string.Empty;
                    ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputText("out_sound##action-pf-out", ref outS, 256)) { action.OutSound = outS; _dirty = true; }
                    var showD = action.ShowDistance ?? false;
                    if (ImGui.Checkbox("距離をオーバーレイ表示##action-pf-show", ref showD)) { action.ShowDistance = showD; _dirty = true; }
                    DrawSafeZoneSubEditor(action);
                    break;
                }
                case "chain_trigger":
                {
                    var tid = action.TriggerId ?? string.Empty;
                    ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputText("trigger_id##action-ct-id", ref tid, 128)) { action.TriggerId = tid; _dirty = true; }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("連鎖発動するトリガーの id（同 zone 内のもの）");
                    // 同ファイル内のトリガー候補
                    if (_workingCopy is not null)
                    {
                        ImGui.SameLine();
                        if (ImGui.SmallButton("選択##action-ct-pick"))
                        {
                            ImGui.OpenPopup("chain-trigger-pick");
                        }
                        if (ImGui.BeginPopup("chain-trigger-pick"))
                        {
                            foreach (var t in _workingCopy.Triggers)
                            {
                                if (string.IsNullOrEmpty(t.Id)) continue;
                                if (ImGui.Selectable($"{t.Id}{(string.IsNullOrEmpty(t.Name) ? "" : $"  ({t.Name})")}"))
                                {
                                    action.TriggerId = t.Id;
                                    _dirty = true;
                                }
                            }
                            ImGui.EndPopup();
                        }
                    }
                    break;
                }
                case "set_variable":
                {
                    // set_variable はトリガー定義側の trigger.set_variable で扱う設計のため、
                    // アクションとして使うパスは JSON 直編集を推奨する旨を示す
                    ImGui.TextWrapped("set_variable はトリガー定義側の trigger.set_variable で設定するのが標準です。" +
                                      "アクションとして使う場合は JSON を直接編集してください。");
                    break;
                }
                default:
                    ImGui.TextDisabled($"({action.Type} は専用 UI 未実装。JSON 直接編集を推奨)");
                    break;
            }

            // 共通：delay
            var delay = (float)action.Delay;
            ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
            if (ImGui.InputFloat("delay (s)##action-delay", ref delay))
            {
                action.Delay = MathF.Max(0, delay);
                _dirty = true;
            }
            ImGui.Unindent(20f);
            ImGui.Spacing();
            ImGui.PopID();
        }

        if (ImGui.Button("アクション追加"))
        {
            trigger.Actions.Add(new ActionDefinition { Type = "tts" });
            _dirty = true;
        }
        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();
        ImGui.TextDisabled("クイック追加：");
        ImGui.SameLine();
        DrawQuickAddButtons(trigger);
    }

    private void BeginAutoGeneration()
    {
        if (_workingCopy is null || _autoGenerator is null)
        {
            return;
        }
        var agg = _recordingScanner.Aggregate(_workingZone);
        var party = _recordingScanner.ListPartyMembers(_workingZone);
        _pendingAutoGen = _autoGenerator.Generate(agg, _workingCopy, party);

        // 攻略タブ側にも下書きを作る：trigger だけ作って攻略タブに何も無いと、
        // 「自動的にできるところまでやってほしい」というユーザー意図に反するため。
        _pendingStrategyProfile = StrategyPlanResolver.SelectActiveProfile(_workingCopy)
            ?? (_workingCopy.StrategyProfiles.Count > 0 ? _workingCopy.StrategyProfiles[0] : null);
        if (_pendingStrategyProfile is not null)
        {
            var partySet = new HashSet<string>(party, StringComparer.OrdinalIgnoreCase);
            _pendingStrategyDrafts = StrategyDraftGenerator.Generate(agg, _pendingStrategyProfile, partySet);
        }
        else
        {
            _pendingStrategyDrafts = null;
        }
        ImGui.OpenPopup("auto-gen-preview");
    }

    private void DrawAutoGenPreviewPopup()
    {
        ImGui.SetNextWindowSize(new Vector2(640f * ImGuiHelpers.GlobalScale, 540f * ImGuiHelpers.GlobalScale));
        if (!ImGui.BeginPopupModal("auto-gen-preview", ImGuiWindowFlags.NoCollapse))
        {
            return;
        }
        if (_workingCopy is null || _pendingAutoGen is null)
        {
            ImGui.EndPopup();
            return;
        }

        var result = _pendingAutoGen;
        var strategyDrafts = _pendingStrategyDrafts;
        ImGui.TextWrapped(
            "録画にあったボスのキャストごとに、Lumina の AoE データを参照して" +
            "「TTS + 視覚通知（円形 AoE / 外周回避 / コーン等）」を含むトリガーを生成します。" +
            "あわせて攻略タブ側にも下書きメカニクスを追加します（散開ポジ等は後で微修正してください）。" +
            "「適用」を押すと作業コピーに追加されます（保存はあなたが「保存」ボタンを押すまで反映されません）。");
        ImGui.Spacing();
        var strategyCount = strategyDrafts?.Generated.Count ?? 0;
        ImGui.TextColored(new Vector4(0.5f, 0.95f, 0.55f, 1f),
            $"生成予定: トリガー {result.Generated.Count} 件 / 攻略メカニクス {strategyCount} 件" +
            $"  スキップ: {result.Skipped.Count} 件");
        ImGui.Spacing();

        if (ImGui.BeginChild("##auto-gen-list", new Vector2(-1, 380f * ImGuiHelpers.GlobalScale), true))
        {
            if (result.Generated.Count > 0)
            {
                ImGui.TextColored(new Vector4(0.6f, 0.85f, 1f, 1f), "新規トリガー（生成予定）：");
                foreach (var t in result.Generated)
                {
                    var actionTypes = string.Join(" + ", t.Actions.ConvertAll(a => a.Type));
                    ImGui.Bullet();
                    ImGui.SameLine(0, 0);
                    ImGui.TextWrapped($"{t.Name}  ({t.Match?.CastId})  → [{actionTypes}]");
                }
                ImGui.Spacing();
            }
            if (strategyDrafts is not null && strategyDrafts.Generated.Count > 0)
            {
                ImGui.Spacing();
                ImGui.TextColored(new Vector4(1f, 0.8f, 0.5f, 1f),
                    $"攻略メカニクス下書き（プロファイル: {_pendingStrategyProfile?.Name ?? _pendingStrategyProfile?.Id ?? "?"}）：");
                foreach (var m in strategyDrafts.Generated)
                {
                    ImGui.Bullet();
                    ImGui.SameLine(0, 0);
                    ImGui.TextWrapped($"{m.Label}  ({m.SourceEventType})");
                }
            }
            if (result.Skipped.Count > 0)
            {
                ImGui.Spacing();
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "スキップ（既存と重複など）：");
                foreach (var s in result.Skipped)
                {
                    ImGui.Bullet();
                    ImGui.SameLine(0, 0);
                    ImGui.TextWrapped(s);
                }
            }
        }
        ImGui.EndChild();

        ImGui.Spacing();
        var totalCount = result.Generated.Count + (strategyDrafts?.Generated.Count ?? 0);
        if (ImGui.Button($"{totalCount} 件を作業コピーに追加",
            new Vector2(280f * ImGuiHelpers.GlobalScale, 0)))
        {
            foreach (var t in result.Generated)
            {
                _workingCopy.Triggers.Add(t);
            }
            if (strategyDrafts is not null && _pendingStrategyProfile is not null)
            {
                foreach (var m in strategyDrafts.Generated)
                {
                    _pendingStrategyProfile.Mechanics.Add(m);
                }
            }
            _dirty = true;
            _pendingAutoGen = null;
            _pendingStrategyDrafts = null;
            _pendingStrategyProfile = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("キャンセル##auto-gen-cancel"))
        {
            _pendingAutoGen = null;
            _pendingStrategyDrafts = null;
            _pendingStrategyProfile = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    /// <summary>
    /// よく使うアクション組合せを 1 クリックで追加するクイック追加ボタン群。
    /// 「カータライズの範囲を出したい」のような典型ケースを最短で組める。
    /// </summary>
    private void DrawQuickAddButtons(TriggerDefinition trigger)
    {
        var castName = trigger.Match?.CastName ?? trigger.Name ?? "AoE";

        if (ImGui.SmallButton("ボス AoE 円##qa-aoe"))
        {
            // 「ボス位置を中心とする円形 AoE」を field_marker として追加
            trigger.Actions.Add(new ActionDefinition
            {
                Type = "field_marker",
                Shape = "circle",
                Radius = 8.0,
                Duration = 5.0,
                Color = "#FF6464",
                SafeZone = new SafeZoneCalculation { Method = "boss_relative" },
            });
            _dirty = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(
            "ボス中心の赤い円をフィールドに描画。\n" +
            "半径 8m / 持続 5 秒 / safe_zone=boss_relative。\n" +
            "保存後は半径や色を編集可能。");

        ImGui.SameLine();
        if (ImGui.SmallButton("ボス前方コーン##qa-cone"))
        {
            trigger.Actions.Add(new ActionDefinition
            {
                Type = "arena_view",
                Gimmick = "cone",
                Direction = "N",
                FanDeg = 90,
                Callout = $"前方回避：{castName}",
                Duration = 5.0,
                ArenaRadius = 20.0,
            });
            _dirty = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(
            "俯瞰アリーナ図に「北向きの 90 度扇形」を表示。\n" +
            "実際のボスの向きと合わせるには direction を編集。");

        ImGui.SameLine();
        if (ImGui.SmallButton("外周回避##qa-outer"))
        {
            trigger.Actions.Add(new ActionDefinition
            {
                Type = "arena_view",
                Gimmick = "outer_ring",
                Callout = $"中央安置：{castName}",
                Duration = 5.0,
                ArenaRadius = 20.0,
            });
            _dirty = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(
            "俯瞰アリーナ図に「外周赤・中央緑」の安置パターンを表示。\n" +
            "全体 AoE で中央に集まるタイプ向け。");

        ImGui.SameLine();
        if (ImGui.SmallButton("散開##qa-scatter"))
        {
            trigger.Actions.Add(new ActionDefinition
            {
                Type = "arena_view",
                Gimmick = "scatter",
                Callout = $"散開：{castName}",
                Duration = 5.0,
                ArenaRadius = 20.0,
            });
            _dirty = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(
            "俯瞰アリーナ図に「4 方向散開」を表示。");

        ImGui.SameLine();
        if (ImGui.SmallButton("タイマーバー##qa-timer"))
        {
            trigger.Actions.Add(new ActionDefinition
            {
                Type = "timer_bar",
                Label = castName,
                Duration = 5.0,
                Color = "#FBBF24",
            });
            _dirty = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(
            "画面右下にカウントダウンバー（持続 5 秒）。\n" +
            "デバフや次イベントまでの時間表示に。");
    }

    private void DrawAggregatePanel(string zone)
    {
        var agg = _recordingScanner.Aggregate(zone);
        ImGui.TextDisabled($"録画ファイル {agg.RecordingFileCount} / 戦闘 {agg.BattleCount} / 総イベント {agg.TotalEventCount}");
        // タイプ別件数を表示。AA / アクションが 0 なら録画段階で取れていない、
        // 数値があるのに表示されないならフィルタの問題と切り分けできる。
        var typeCounts = agg.Events
            .GroupBy(e => e.Key.Type)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Count));
        var castN = typeCounts.GetValueOrDefault("cast_start", 0);
        var actionN = typeCounts.GetValueOrDefault("action_used", 0);
        var autoAttackN = typeCounts.GetValueOrDefault("auto_attack", 0);
        var statusN = typeCounts.GetValueOrDefault("status_gain", 0)
                      + typeCounts.GetValueOrDefault("status_update", 0)
                      + typeCounts.GetValueOrDefault("status_lose", 0);
        var hpN = typeCounts.GetValueOrDefault("hp_change", 0);
        ImGui.TextDisabled($"内訳：cast_start={castN} / action_used={actionN} / auto_attack={autoAttackN} / status={statusN} / hp_change={hpN}");
        if (actionN == 0 && autoAttackN == 0 && agg.TotalEventCount > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.7f, 0.4f, 1f));
            ImGui.TextWrapped("⚠ action_used が 0 件です。AA / 即時アクションが録画されてません。" +
                              "プラグイン再読み込み (/xlrestart) → 1 戦してから再確認してください。");
            ImGui.PopStyleColor();
        }
        ImGui.SameLine(0, 24f);
        ImGui.Checkbox("タイムライン表示", ref _aggregateAsTimeline);
        ImGui.SameLine(0, 24f);
        ImGui.Checkbox("自分・PT のイベントを隠す", ref _hideSelfEvents);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("status_gain / status_update / hp_change のうち、target / source が PT メンバー名のものを除外。\n" +
                             "ボスのキャストやステータスだけに絞れる。");
        }
        ImGui.SameLine(0, 24f);
        ImGui.Checkbox("status を隠す", ref _hideStatusEvents);
        ImGui.SameLine(0, 24f);
        ImGui.Checkbox("環境ノイズを隠す", ref _hideEnvironmentalNoise);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "「その他」レーンに混じる以下を非表示にする：\n" +
                "・type=unknown（古い録画ファイルの遺物）\n" +
                "・object_appear / object_disappear のうち 1 戦あたり 5 回超で出てくるもの\n" +
                "  （脱出地点・秘紋・装飾物・ボスの分割パーツ ×N など。\n" +
                "    ギミック性が無い背景オブジェクト扱い）");
        }
        ImGui.Spacing();

        // フィルタ後のイベントを生成
        var filteredEvents = ApplyEventFilters(agg);

        if (filteredEvents.Count == 0)
        {
            ImGui.TextWrapped("表示できるイベントがありません。フィルタを緩めるか、" +
                "/echoes record on で録画してください。");
            return;
        }

        if (_aggregateAsTimeline)
        {
            // タイムライン表示では「同名（cast_id 違い）が同タイミングに並ぶ」のは
            // 視覚的ノイズなので dedup する。最も観測回数が多い 1 件を残す。
            // テーブル表示では各 cast_id を独立に扱いたい（個別のトリガー作成等に使う）
            // ので dedup しない。
            var dedupedForTimeline = DedupSameNameSameTime(filteredEvents);
            var filteredAgg = new Recording.AggregatedEvents(
                dedupedForTimeline, agg.BattleCount, agg.TotalEventCount, agg.RecordingFileCount);
            _timelineRenderer.Draw(filteredAgg);
            if (_timelineRenderer.SelectedEvent is { } selected)
            {
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.TextUnformatted($"選択: {selected.Type} / {selected.Id ?? selected.Name ?? "—"}");
                ImGui.SameLine();
                if (ImGui.Button("このイベントからトリガー作成"))
                {
                    CreateTriggerFromKey(selected);
                }
                ImGui.SameLine();
                if (ImGui.Button("攻略ギミックに追加"))
                {
                    CreateStrategyMechanicFromKey(selected);
                }
                // ワンクリック AoE 表示：cast_start / action_used / status_gain どれを選んでいても
                // 「これに反応してミニマップ AoE 円を出す」メカニクスを 1 個作る。
                // 旧版は cast_start 限定だったため、AA レーンのアクションを選んでいるユーザーには
                // ボタンが見えなかった。条件を緩めて全イベントタイプで利用可能に。
                var canQuickAoe =
                    !string.IsNullOrEmpty(selected.Id) &&
                    (selected.Type == "cast_start" ||
                     selected.Type == "action_used" ||
                     selected.Type == "auto_attack" ||
                     selected.Type == "status_gain" ||
                     selected.Type == "object_appear");
                if (canQuickAoe)
                {
                    ImGui.SameLine();
                    ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.20f, 0.55f, 0.20f, 1f));
                    if (ImGui.Button("ミニマップに AoE 表示##quick-aoe"))
                    {
                        QuickAddAoeMechanic(selected);
                        _lastRaidWideMessage = $"AoE 付きメカニクスを作成しました（攻略タブで半径などを編集可能）";
                    }
                    ImGui.PopStyleColor();
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(
                            "選択したイベントに反応する『中央ボス位置にデフォルト 8m 円』の\n" +
                            "メカニクスを 1 クリックで作成します。\n" +
                            "攻略タブを開いて以下を調整してください：\n" +
                            "・AoE の形（円／ドーナツ／扇形／矩形／直線）\n" +
                            "・半径・回転角・基準（ボス位置／ターゲット／ウェイマーク等）\n" +
                            "・複製ボタンでランダム分岐用の対の AoE も作れる");
                    }
                }
                if ((selected.Type == "cast_start" || selected.Type == "action_used") &&
                    !string.IsNullOrEmpty(selected.Id))
                {
                    ImGui.SameLine();
                    if (ImGui.Button("全体攻撃にマーク##mark-raidwide"))
                    {
                        MarkSelectedAsRaidWide(selected);
                        // 辞書だけだと既存メカニクスの DisableMinimap に反映されない場合があるので、
                        // 開いている trigger ファイル内で同じ cast / action を参照しているメカニクスを
                        // 直接 DisableMinimap=true にして「ミニマップ出さない」を確実化する。
                        var propagated = PropagateRaidWideToMechanics(selected);
                        _lastRaidWideMessage = propagated > 0
                            ? $"全体攻撃マーク + {propagated} 件のメカニクスを「ミニマップ非表示」に設定"
                            : "全体攻撃マーク完了（該当メカニクスなし）";
                    }
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(
                            "このキャストを、このコンテンツ専用の「全体攻撃」として登録する。\n" +
                            "・登録後はミニマップに範囲を描画しない（回避不能扱い）\n" +
                            "・既に登録済みのメカニクスがこの cast を参照していたら、\n" +
                            "  そのメカニクスの『ミニマップを出さない』も自動 ON にする\n" +
                            "・TTS / オーバーレイの読み上げは引き続き動く\n" +
                            "・間違ってマークした場合は「ファイル設定」タブの\n" +
                            "  「全体攻撃マーク済みキャスト」一覧から「解除」できる");
                    }
                    if (!string.IsNullOrEmpty(_lastRaidWideMessage))
                    {
                        ImGui.SameLine();
                        ImGui.TextDisabled(_lastRaidWideMessage);
                    }
                }
            }
            return;
        }

        if (ImGui.BeginTable("##aggregate-table", 7,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp |
            ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY))
        {
            ImGui.TableSetupColumn("種別", ImGuiTableColumnFlags.WidthFixed, 100f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthFixed, 100f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("名前", ImGuiTableColumnFlags.WidthStretch, 2.0f);
            ImGui.TableSetupColumn("発動者/対象", ImGuiTableColumnFlags.WidthStretch, 1.5f);
            ImGui.TableSetupColumn("観測回数", ImGuiTableColumnFlags.WidthFixed, 80f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("初回時刻", ImGuiTableColumnFlags.WidthFixed, 90f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("攻略", ImGuiTableColumnFlags.WidthFixed, 86f * ImGuiHelpers.GlobalScale);
            ImGui.TableHeadersRow();

            foreach (var ev in filteredEvents)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(ev.Key.Type);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(ev.Key.Id ?? "—");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(ev.Key.Name ?? "—");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(ev.Key.Source ?? ev.Key.Target ?? "—");
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{ev.Count}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{ev.FirstSeenSeconds:0.0}s");
                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"追加##strategy-{ev.Key.Type}-{ev.Key.Id}-{ev.FirstSeenSeconds:0.0}"))
                {
                    CreateStrategyMechanicFromEvent(ev);
                }
            }

            ImGui.EndTable();
        }
    }

    private void CreateTriggerFromKey(EventKey key)
    {
        if (_workingCopy is null)
        {
            return;
        }
        // 既存トリガーが同じキーを持っているかは厳密には判定しないが、
        // 重複 ID 防止のためサフィックスを付ける
        var baseId = SuggestId(key);
        var id = baseId;
        var n = 1;
        while (_workingCopy.Triggers.Any(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            n++;
            id = $"{baseId}_{n}";
        }

        var trigger = new TriggerDefinition
        {
            Id = id,
            Type = key.Type,
            Match = BuildMatchFromKey(key),
            Actions = new List<ActionDefinition>
            {
                new() { Type = "tts", Text = key.Name ?? key.Id ?? key.Type },
            },
        };
        _workingCopy.Triggers.Add(trigger);
        _editingTriggerId = trigger.Id;
        _dirty = true;
        _tabContext.PendingFocusTab = null; // 集計タブに留まる
    }

    /// <summary>
    /// 「録画から ID を選ぶ」ボタン → popup を開いて、aggregate から
    /// 該当タイプ（cast_start / status_gain / action_used）の観測イベントを
    /// 「名前: ID (×観測回数)」形式で一覧、クリックで選択する。
    /// 選択結果は <paramref name="onSelected"/>(id, name) コールバックで受け取る。
    /// </summary>
    private void OpenIdPicker(string eventType, Action<string?, string?> onSelected)
    {
        _idPickerType = eventType;
        _idPickerCallback = onSelected;
        _idPickerSearch = string.Empty;
        _shouldOpenIdPicker = true;
        Plugin.Log.Information("[FfxivEchoes] OpenIdPicker: type={Type}", eventType);
    }

    private void DrawIdPickerPopup()
    {
        // popup ではなく独立した通常 ImGui ウィンドウとして開く。
        // BeginPopupModal は親 ID stack や BeginChild との相性で動かないケースがあるため、
        // Begin/End ベースに切り替えて確実に表示する。
        if (_shouldOpenIdPicker)
        {
            _shouldOpenIdPicker = false;
            Plugin.Log.Information("[FfxivEchoes] IdPicker open type={Type}", _idPickerType ?? "?");
        }
        if (_idPickerType is null) return;

        var open = true;
        ImGui.SetNextWindowSize(new Vector2(560f * ImGuiHelpers.GlobalScale, 480f * ImGuiHelpers.GlobalScale),
            ImGuiCond.Appearing);
        ImGui.SetNextWindowFocus();
        if (!ImGui.Begin("録画から選ぶ##id-picker-window", ref open,
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.End();
            return;
        }
        if (!open)
        {
            _idPickerType = null;
            _idPickerCallback = null;
            ImGui.End();
            return;
        }

        var label = _idPickerType switch
        {
            "cast_start" => "録画から「キャスト」を選ぶ",
            "status_gain" => "録画から「ステータス（バフ／デバフ）」を選ぶ",
            "action_used" => "録画から「アクション」を選ぶ",
            "object_appear" => "録画から「出現オブジェクト」を選ぶ",
            _ => "録画から選ぶ",
        };
        ImGui.TextUnformatted(label);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##id-picker-search", "検索（名前・ID 部分一致）", ref _idPickerSearch, 64);
        ImGui.Spacing();

        Recording.AggregatedEvents agg;
        try
        {
            agg = _recordingScanner.Aggregate(_workingZone);
        }
        catch
        {
            ImGui.TextDisabled("録画読み込み失敗");
            ImGui.EndPopup();
            return;
        }

        // 該当タイプのイベントを集める。status はステータス系まとめて見る
        var matchTypes = _idPickerType switch
        {
            "cast_start" => new[] { "cast_start" },
            "status_gain" => new[] { "status_gain", "status_update", "status_lose" },
            "action_used" => new[] { "action_used" },
            "object_appear" => new[] { "object_appear", "object_disappear" },
            _ => Array.Empty<string>(),
        };

        var rows = agg.Events
            .Where(e => Array.IndexOf(matchTypes, e.Key.Type) >= 0)
            .Where(e => !string.IsNullOrEmpty(e.Key.Id))
            .GroupBy(e => (e.Key.Id, e.Key.Name))
            .Select(g => new
            {
                Id = g.Key.Id,
                Name = g.Key.Name ?? "(無名)",
                Count = g.Sum(x => x.Count),
            })
            .Where(r =>
                string.IsNullOrEmpty(_idPickerSearch) ||
                (r.Name?.Contains(_idPickerSearch, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (r.Id?.Contains(_idPickerSearch, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderByDescending(r => r.Count)
            .Take(120)
            .ToList();

        if (rows.Count == 0)
        {
            ImGui.TextDisabled("録画データがまだ無いか、フィルタに一致しません。\n" +
                                "（コンテンツに入って 1 戦すると候補が出ます）");
        }
        else
        {
            if (ImGui.BeginTable("##id-picker-table", 3,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
                new Vector2(0, 320f * ImGuiHelpers.GlobalScale)))
            {
                ImGui.TableSetupColumn("名前", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthFixed, 100f * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("観測", ImGuiTableColumnFlags.WidthFixed, 70f * ImGuiHelpers.GlobalScale);
                ImGui.TableHeadersRow();
                foreach (var r in rows)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    if (ImGui.Selectable($"{r.Name}##pick-{r.Id}", false,
                        ImGuiSelectableFlags.SpanAllColumns))
                    {
                        _idPickerCallback?.Invoke(r.Id, r.Name);
                        _idPickerType = null;
                        _idPickerCallback = null;
                    }
                    ImGui.TableNextColumn();
                    ImGui.TextDisabled(r.Id ?? "—");
                    ImGui.TableNextColumn();
                    ImGui.TextDisabled($"×{r.Count}");
                }
                ImGui.EndTable();
            }
        }

        ImGui.Spacing();
        if (ImGui.Button("閉じる"))
        {
            _idPickerType = null;
            _idPickerCallback = null;
        }
        ImGui.End();
    }

    /// <summary>
    /// メカニクスを戦闘中に発動したのと同じ流れでイベントバスに publish する。
    /// 編集中のレイアウト確認用。
    /// </summary>
    private void FireMechanicPreview(StrategyProfile profile, MechanicStrategy mechanic)
    {
        if (_eventBus is null)
        {
            return;
        }
        var actions = StrategyPlanResolver.BuildReminderActions(_workingCopy, profile, mechanic);
        if (actions.Count == 0)
        {
            return;
        }
        var now = DateTimeOffset.UtcNow;
        _eventBus.Publish(new Events.TriggerFiredEvent(
            Timestamp: now,
            Zone: _workingZone,
            TriggerId: $"__preview_{profile.Id}_{mechanic.Id}",
            TriggerName: $"プレビュー: {mechanic.Label}",
            Actions: actions,
            SourceEvent: new Events.CombatStartedEvent(now)));
    }

    private string? _lastRaidWideMessage;

    /// <summary>
    /// 「全体攻撃にマーク」したキャストを参照しているメカニクスを scan して、
    /// <see cref="MechanicStrategy.DisableMinimap"/> を強制 true に書き換える。
    /// </summary>
    /// <remarks>
    /// 辞書側の RaidWide フラグだけでは、エッジケース（status 起点のトリガー、
    /// 過去に保存された arena_view 設定が残ったまま等）でミニマップが残る。
    /// メカニクス側に直接 DisableMinimap を立てれば、どのトリガー経路でも確実に出ない。
    /// </remarks>
    /// <returns>更新したメカニクス件数</returns>
    private int PropagateRaidWideToMechanics(EventKey selected)
    {
        if (_workingCopy is null) return 0;
        var rawId = selected.Id;
        if (string.IsNullOrEmpty(rawId)) return 0;
        if (!AoeResolver.TryParseCastId(rawId, out var targetId)) return 0;
        var targetName = selected.Name;
        var changed = 0;
        foreach (var profile in _workingCopy.StrategyProfiles)
        {
            foreach (var mech in profile.Mechanics)
            {
                if (mech.DisableMinimap) continue;
                if (MatchesRaidWideTarget(mech.AttachedTo, targetId, targetName) ||
                    mech.Triggers.Any(t => MatchesRaidWideTarget(t.Match, targetId, targetName)))
                {
                    mech.DisableMinimap = true;
                    changed++;
                }
            }
        }
        if (changed > 0)
        {
            _dirty = true;
        }
        return changed;
    }

    /// <summary>マッチ条件が指定 cast/action を参照しているか（id 完全一致 or name 完全一致）。</summary>
    private static bool MatchesRaidWideTarget(MatchCondition? match, uint targetId, string? targetName)
    {
        if (match is null) return false;
        if (!string.IsNullOrEmpty(match.CastId) &&
            AoeResolver.TryParseCastId(match.CastId, out var cId) && cId == targetId)
        {
            return true;
        }
        if (!string.IsNullOrEmpty(match.ActionId) &&
            AoeResolver.TryParseCastId(match.ActionId, out var aId) && aId == targetId)
        {
            return true;
        }
        if (!string.IsNullOrEmpty(targetName))
        {
            if (string.Equals(match.CastName, targetName, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(match.ActionName, targetName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private void MarkSelectedAsRaidWide(EventKey selected)
    {
        if (_workingCopy is null) return;
        var key = NormalizeRaidWideKey(selected.Id);
        if (string.IsNullOrEmpty(key)) return;
        if (!AoeResolver.TryParseCastId(key, out var actionId)) return;

        var existing = AutoSafeCallPlanner.FindRaidWideMarker(_workingCopy, actionId, selected.Name);
        if (existing is null)
        {
            _workingCopy.RaidWideMarkers.Add(new RaidWideMarker
            {
                Id = key,
                Name = selected.Name,
                Source = "manual",
                Callout = $"全体: {selected.Name ?? key}",
                Tts = selected.Name ?? "全体攻撃",
            });
        }
        else
        {
            existing.Id = key;
            existing.Name ??= selected.Name;
            existing.Source = "manual";
            existing.Callout ??= $"全体: {selected.Name ?? key}";
            existing.Tts ??= selected.Name ?? "全体攻撃";
        }

        _dirty = true;
    }

    private static string NormalizeRaidWideKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        return AoeResolver.TryParseCastId(raw, out var id)
            ? $"0x{id:X4}"
            : raw.Trim();
    }

    private void CreateStrategyMechanicFromKey(EventKey key)
    {
        if (_workingCopy is null)
        {
            return;
        }

        var ev = _recordingScanner.Aggregate(_workingZone).Events.FirstOrDefault(e => e.Key.Equals(key));
        if (ev is not null)
        {
            CreateStrategyMechanicFromEvent(ev);
            return;
        }

        var profile = EnsureActiveStrategyProfile();
        var id = UniqueMechanicId(profile, SuggestMechanicPrefix(key));
        var prediction = new RecordingTimelinePrediction(
            EventType: key.Type,
            RelativeSeconds: 0,
            Label: key.Name ?? key.Id ?? key.Type,
            Id: key.Id ?? string.Empty,
            Source: key.Source,
            Target: key.Target,
            ObservedCount: 1,
            OccurrenceIndex: 0,
            OccurrenceSeenCount: 1,
            Confidence: 1.0,
            TimeJitterSeconds: 0);
        profile.Mechanics.Add(StrategyPlanResolver.CreateMechanicDraft(prediction, id));
        _dirty = true;
    }

    /// <summary>
    /// 1 クリックで「指定イベントに反応 + 中央ボス位置に 8m 円 AoE」のメカニクスを作成する。
    /// cast_start / action_used / status_gain / object_appear すべてに対応。
    /// </summary>
    private void QuickAddAoeMechanic(EventKey key)
    {
        if (_workingCopy is null) return;
        var profile = EnsureActiveStrategyProfile();

        // ベースとなるメカニクスを既存ロジックで作成（attached_to / cast_id 等が正しく入る）
        var prefix = SuggestMechanicPrefix(key);
        var id = UniqueMechanicId(profile, prefix);
        var label = key.Name ?? key.Id ?? key.Type;
        var prediction = new RecordingTimelinePrediction(
            EventType: key.Type,
            RelativeSeconds: 0,
            Label: label,
            Id: key.Id ?? string.Empty,
            Source: key.Source,
            Target: key.Target,
            ObservedCount: 1,
            OccurrenceIndex: 0,
            OccurrenceSeenCount: 1,
            Confidence: 1.0,
            TimeJitterSeconds: 0);
        var mechanic = StrategyPlanResolver.CreateMechanicDraft(prediction, id, profile);

        // 発動条件：cast_start なら AttachedTo（legacy path）、それ以外は明示的 Triggers が必要
        // （MechanicTriggerService.OnActionUsed / OnStatusGained 等は AttachedTo を見ない）
        switch (key.Type)
        {
            case "action_used":
            case "auto_attack":
                mechanic.Triggers.Add(new MechanicTrigger
                {
                    Type = "action_used",
                    Match = new MatchCondition
                    {
                        ActionId = key.Id,
                        ActionName = key.Name,
                    },
                });
                break;
            case "status_gain":
                mechanic.Triggers.Add(new MechanicTrigger
                {
                    Type = "status_gain",
                    Match = new MatchCondition
                    {
                        StatusId = uint.TryParse(key.Id, out var sid) ? sid : null,
                        StatusName = key.Name,
                    },
                });
                break;
            case "object_appear":
                mechanic.Triggers.Add(new MechanicTrigger
                {
                    Type = "object_group",
                    ActorName = key.Name,
                    ActorDataId = uint.TryParse(key.Id, out var did) ? did : null,
                    ObjectCountMin = 1,
                    ObjectWindowSec = 1.5,
                    DedupSec = 5.0,
                });
                break;
            // cast_start は AttachedTo の legacy path で発火するので Triggers 追加不要
        }

        // デフォルト AoE：中央に 8m 円（危険）。基準=source_actor でボス位置に追従。
        mechanic.AoeZones.Add(new StrategyAoeZone
        {
            Id = "aoe_main",
            Label = label,
            Shape = "circle",
            X = 0,
            Z = 0,
            RadiusM = 8.0,
            IsDanger = true,
            Anchor = "source_actor",
        });
        // ミニマップ確実に出すフラグ
        mechanic.DisableMinimap = false;
        // user_layout 経路に乗せる（gimmick null → AoeZones 描画）
        mechanic.Gimmick = null;

        profile.Mechanics.Add(mechanic);
        _dirty = true;
    }

    private void CreateStrategyMechanicFromEvent(AggregatedEvent ev)
    {
        var profile = EnsureActiveStrategyProfile();
        var agg = _recordingScanner.Aggregate(_workingZone);
        var battleCount = Math.Max(1, agg.BattleCount);
        var occurrences = RecordingPredictionPlanner.GetOccurrences(ev);
        var prefix = SuggestMechanicPrefix(ev.Key);
        foreach (var occurrence in occurrences)
        {
            var id = UniqueMechanicId(profile, prefix);
            var baseLabel = ev.Key.Name ?? ev.Key.Id ?? ev.Key.Type;
            var prediction = new RecordingTimelinePrediction(
                EventType: ev.Key.Type,
                RelativeSeconds: occurrence.RepresentativeTimeSeconds,
                Label: occurrences.Count > 1 ? $"{baseLabel} #{occurrence.Index + 1}" : baseLabel,
                Id: ev.Key.Id ?? string.Empty,
                Source: ev.Key.Source,
                Target: ev.Key.Target,
                ObservedCount: ev.Count,
                OccurrenceIndex: occurrence.Index,
                OccurrenceSeenCount: occurrence.SeenCount,
                Confidence: Math.Clamp((double)occurrence.SeenCount / battleCount, 0.0, 1.0),
                TimeJitterSeconds: CalculateEventJitter(occurrence.ObservedTimesSeconds));
            profile.Mechanics.Add(StrategyPlanResolver.CreateMechanicDraft(prediction, id));
        }
        _dirty = true;
    }

    private StrategyProfile EnsureActiveStrategyProfile()
    {
        if (_workingCopy is null)
        {
            throw new InvalidOperationException("No working trigger file.");
        }

        var profile = StrategyPlanResolver.SelectActiveProfile(_workingCopy);
        if (profile is not null)
        {
            return profile;
        }

        profile = CreateDefaultStrategyProfile("default", "Default party strategy");
        _workingCopy.StrategyProfiles.Add(profile);
        _workingCopy.ActiveStrategyProfileId = profile.Id;
        return profile;
    }

    private static string SuggestMechanicPrefix(EventKey key)
    {
        var raw = key.Name ?? key.Id ?? key.Type;
        var normalized = new string(raw
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray()).Trim('_');
        return string.IsNullOrWhiteSpace(normalized) ? "mechanic" : normalized;
    }

    private static double CalculateEventJitter(IReadOnlyList<double> times)
    {
        if (times.Count <= 1)
        {
            return 0;
        }

        return times.Max() - times.Min();
    }

    private static string SuggestId(EventKey key) => key.Type switch
    {
        "cast_start" or "cast_complete" or "cast_cancel" => $"cast_{key.Id ?? key.Name ?? "x"}".ToLowerInvariant().Replace(" ", "_"),
        "action_used" or "auto_attack" => $"action_{key.Id ?? key.Name ?? "x"}".ToLowerInvariant().Replace(" ", "_"),
        "status_gain" or "status_lose" or "status_update" => $"status_{key.Id ?? key.Name ?? "x"}".ToLowerInvariant().Replace(" ", "_"),
        "hp_change" => $"hp_{key.Source ?? "actor"}",
        "object_appear" or "object_disappear" => $"object_{key.Id ?? key.Name ?? "x"}".ToLowerInvariant().Replace(" ", "_"),
        _ => key.Type,
    };

    private static MatchCondition BuildMatchFromKey(EventKey key)
    {
        var m = new MatchCondition();
        switch (key.Type)
        {
            case "cast_start":
            case "cast_complete":
            case "cast_cancel":
                m.CastId = key.Id;
                m.CastName = key.Name;
                m.Source = key.Source;
                break;
            case "status_gain":
            case "status_lose":
            case "status_update":
                if (key.Id is not null && uint.TryParse(key.Id, out var statusId))
                {
                    m.StatusId = statusId;
                }
                m.StatusName = key.Name;
                if (key.Target is not null)
                {
                    m.Target = new TargetSpec(new List<string> { key.Target });
                }
                break;
            case "action_used":
            case "auto_attack":
                m.ActionId = key.Id;
                m.ActionName = key.Name;
                m.Source = key.Source;
                break;
            case "hp_change":
                m.Actor = key.Source;
                break;
            case "object_appear":
            case "object_disappear":
                m.Actor = key.Name ?? key.Source;
                break;
        }
        return m;
    }

    private void DrawStrategyPanel()
    {
        if (_workingCopy is null)
        {
            return;
        }

        // 上部：簡潔な要約 + 「使わなくていい」明示
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.85f, 0.4f, 1f));
        ImGui.TextWrapped("■ このタブは “PT 攻略の覚え書き” を作る場所");
        ImGui.PopStyleColor();
        ImGui.TextWrapped(
            "「ホリッドロアの 5 秒前にランパート使う」「散開のときタンクは北、ヒラは東」など、\n" +
            "PT 内で決まっている動きを保存しておくと、戦闘中に自動で読み上げ + ミニマップ表示してくれる。");
        ImGui.Spacing();
        ImGui.TextDisabled(
            "※ 単純に音だけ鳴らしたいだけなら「トリガー一覧」タブで足りる。\n" +
            "※ ここは「PT 全員のポジション」「メカニクスごとに誰が何する」を細かく書きたい人向け。");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (_workingCopy.StrategyProfiles.Count == 0)
        {
            if (ImGui.Button("デフォルトプロファイル作成"))
            {
                _workingCopy.StrategyProfiles.Add(CreateDefaultStrategyProfile("default", "Default party strategy"));
                _workingCopy.ActiveStrategyProfileId = "default";
                _dirty = true;
            }
            return;
        }

        DrawStrategyProfileSelector();
        var profile = StrategyPlanResolver.SelectActiveProfile(_workingCopy) ?? _workingCopy.StrategyProfiles[0];

        ImGui.Spacing();
        ImGui.Separator();
        DrawStrategyProfileEditor(profile);

        // 散開ポジ編集はプロファイル直下では出さない（ユーザー要望：ギミック単位で決めたい）。
        // 各メカニクスの中で個別にミニマップを描く。

        ImGui.Spacing();
        ImGui.Separator();
        if (ImGui.Button("録画から攻略下書きを一括作成"))
        {
            GenerateStrategyDraftsFromRecording(profile);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("このゾーンの録画タイムラインから、ボスキャスト・出現オブジェクト・重要ステータスを\n" +
                             "攻略メカニクスの下書きとして追加します。\n" +
                             "PT 固有の処理、散開位置、AoE 図形は追加後に下の UI で調整します。");
        }
        ImGui.SameLine();
        if (ImGui.Button("🔀 分岐パターンを自動検出"))
        {
            BeginBranchDetection();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("録画ファイルを「最初の cast」でクラスタリングして、\n" +
                             "コンテンツのタイムライン分岐パターンを自動検出します。\n" +
                             "例：滅エヌオーで「エアロジャ系」「フレア系」のような複数パターンを録画しておくと、\n" +
                             "それぞれを TimelineBranch として登録、戦闘中に自動でパターンを判定して\n" +
                             "該当 mechanic だけを表示するようになります。\n" +
                             "**最低 2 件以上の録画が必要**。");
        }
        if (!string.IsNullOrWhiteSpace(_lastStrategyDraftMessage))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(_lastStrategyDraftMessage);
        }
        if (!string.IsNullOrWhiteSpace(_branchApplyResultMessage))
        {
            ImGui.TextDisabled(_branchApplyResultMessage);
        }
        ImGui.Spacing();
        DrawBranchListEditor();
        DrawMechanicStrategiesEditor(profile);
        DrawStrategyAttachPopup();
        DrawIdPickerPopup();
        DrawBranchDetectionPopup();
    }

    /// <summary>
    /// このゾーンの録画ファイルを <see cref="FfxivEchoes.Recording.RecordingBranchAnalyzer"/> に
    /// 渡し、検出結果をモーダルでプレビューできる状態にする。
    /// </summary>
    private void BeginBranchDetection()
    {
        if (_workingCopy is null) return;

        var recordings = _recordingScanner.ListRecordings(_workingZone);
        var paths = recordings.Select(r => r.Path).ToList();

        // 同期実行（数秒で完了する想定。ローダー UI は出さず、結果を即 popup へ）
        var result = FfxivEchoes.Recording.RecordingBranchAnalyzer.Analyze(paths);
        _branchDetectionResult = result;
        _branchDetectionGroupSelected = result.Groups.Select(_ => true).ToArray();
        _branchApplyResultMessage = null;
        _shouldOpenBranchPopup = true;
    }

    /// <summary>
    /// 分岐検出結果のプレビュー / 適用 popup。<see cref="DrawStrategyPanel"/> 末尾から呼ぶ。
    /// </summary>
    private void DrawBranchDetectionPopup()
    {
        if (_shouldOpenBranchPopup)
        {
            ImGui.OpenPopup(BranchDetectionPopupId);
            _shouldOpenBranchPopup = false;
        }

        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(600f, 420f) * ImGuiHelpers.GlobalScale, ImGuiCond.Appearing);

        var open = true;
        if (!ImGui.BeginPopupModal(BranchDetectionPopupId, ref open,
            ImGuiWindowFlags.NoSavedSettings))
        {
            return;
        }

        try
        {
            var result = _branchDetectionResult;
            if (result is null)
            {
                ImGui.TextUnformatted("検出結果がありません。");
                if (ImGui.Button("閉じる")) ImGui.CloseCurrentPopup();
                return;
            }

            ImGui.TextUnformatted($"録画ファイル {result.TotalFilesScanned} 件を解析しました。");
            if (!string.IsNullOrEmpty(result.DiagnosticsMessage))
            {
                ImGui.TextWrapped(result.DiagnosticsMessage);
            }
            ImGui.Spacing();
            ImGui.Separator();

            if (result.NotEnoughData || result.NoBranchDetected || result.Groups.Count == 0)
            {
                ImGui.TextDisabled("分岐は検出されませんでした。");
                if (result.OutlierGroups.Count > 0)
                {
                    ImGui.Spacing();
                    ImGui.TextUnformatted("外れ値（1 ファイルのみ）:");
                    foreach (var g in result.OutlierGroups)
                    {
                        ImGui.BulletText($"{g.FirstCastId} {g.FirstCastName} (1 件)");
                    }
                }
                ImGui.Spacing();
                if (ImGui.Button("閉じる")) ImGui.CloseCurrentPopup();
                return;
            }

            ImGui.TextUnformatted("検出された分岐パターン:");
            for (var i = 0; i < result.Groups.Count; i++)
            {
                var g = result.Groups[i];
                if (i >= _branchDetectionGroupSelected.Length) break;
                var sel = _branchDetectionGroupSelected[i];
                if (ImGui.Checkbox($"##branch-sel-{i}", ref sel))
                {
                    _branchDetectionGroupSelected[i] = sel;
                }
                ImGui.SameLine();
                ImGui.TextUnformatted(
                    $"パターン{i + 1}：判定 {g.FirstCastId} {g.FirstCastName} — " +
                    $"{g.FileCount} 件 (信頼度 {g.Confidence * 100:0}%)");
            }

            if (result.AdditionalGroups.Count > 0)
            {
                ImGui.Spacing();
                ImGui.TextDisabled($"9 種類以上のパターンが検出されました（残り {result.AdditionalGroups.Count} 件は追加候補）:");
                foreach (var g in result.AdditionalGroups)
                {
                    ImGui.BulletText($"{g.FirstCastId} {g.FirstCastName} ({g.FileCount} 件)");
                }
            }
            if (result.OutlierGroups.Count > 0)
            {
                ImGui.Spacing();
                ImGui.TextDisabled("外れ値（1 ファイルのみ、自動除外）:");
                foreach (var g in result.OutlierGroups)
                {
                    ImGui.BulletText($"{g.FirstCastId} {g.FirstCastName}");
                }
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextDisabled("適用すると以下を実行します：");
            ImGui.BulletText("選択したパターンを TriggerFile.Branches に追加");
            ImGui.BulletText("既存メカニクスのうち、特定パターンにのみ出る cast の mechanic に branch_id を自動付与");
            ImGui.BulletText("既に branch_id が設定済みの mechanic は上書きしない");

            ImGui.Spacing();
            ImGui.Checkbox("📝 録画から新規 mechanic も自動生成する", ref _branchDetectionGenerateMechanics);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("ON にすると、適用と同時に「録画から攻略下書きを一括作成」も実行されます。\n" +
                                 "各メカニクスの cast_id が特定 branch にのみ出る場合は branch_id 自動付与、\n" +
                                 "複数 branch にまたがって出現する場合は共通扱い（branch_id=null）。\n" +
                                 "\n" +
                                 "OFF：branches[] と既存 mechanic への branch_id 付与のみ。");
            }

            ImGui.Spacing();
            var anySelected = _branchDetectionGroupSelected.Any(b => b);
            if (!anySelected) ImGui.BeginDisabled();
            if (ImGui.Button("✅ 適用"))
            {
                ApplyBranchDetection();
                ImGui.CloseCurrentPopup();
            }
            if (!anySelected) ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("キャンセル"))
            {
                ImGui.CloseCurrentPopup();
            }
        }
        finally
        {
            ImGui.EndPopup();
        }
    }

    private void ApplyBranchDetection()
    {
        if (_workingCopy is null || _branchDetectionResult is null) return;

        var selected = new List<FfxivEchoes.Recording.BranchGroup>();
        for (var i = 0; i < _branchDetectionResult.Groups.Count; i++)
        {
            if (i < _branchDetectionGroupSelected.Length && _branchDetectionGroupSelected[i])
            {
                selected.Add(_branchDetectionResult.Groups[i]);
            }
        }
        if (selected.Count == 0) return;

        // generateMechanics=true 時：full aggregate と party members を取得して Apply に渡す
        FfxivEchoes.Recording.AggregatedEvents? fullAggregate = null;
        IReadOnlySet<string>? partyMembers = null;
        if (_branchDetectionGenerateMechanics)
        {
            try
            {
                fullAggregate = _recordingScanner.Aggregate(_workingZone);
                partyMembers = new HashSet<string>(
                    _recordingScanner.ListPartyMembers(_workingZone),
                    StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                // 集約失敗時は generateMechanics 無効化（branches[] 追加のみ実行）
                fullAggregate = null;
            }
        }

        var applyResult = TriggerBranchApplier.Apply(
            _workingCopy, _branchDetectionResult, selected,
            generateMechanics: _branchDetectionGenerateMechanics,
            fullAggregate: fullAggregate,
            partyMembers: partyMembers);
        _dirty = true;
        _branchApplyResultMessage = applyResult.MechanicsGenerated > 0
            ? $"分岐 {applyResult.BranchesAdded} 件追加 / 新規 mechanic {applyResult.MechanicsGenerated} 件 / 既存 mechanic {applyResult.MechanicBranchIdsAssigned} 件に branch_id 付与"
            : $"分岐 {applyResult.BranchesAdded} 件追加 / mechanic {applyResult.MechanicBranchIdsAssigned} 件に branch_id 付与";
    }

    /// <summary>
    /// 既存の <see cref="TriggerFile.Branches"/> リストを表示・編集する簡易 UI。
    /// 自動検出後の確認や、不要分岐の削除に使う。
    /// </summary>
    private void DrawBranchListEditor()
    {
        if (_workingCopy is null || _workingCopy.Branches.Count == 0) return;

        if (!ImGui.CollapsingHeader($"タイムライン分岐 ({_workingCopy.Branches.Count} 件)"))
        {
            return;
        }

        ImGui.Indent();
        for (var i = 0; i < _workingCopy.Branches.Count; i++)
        {
            var b = _workingCopy.Branches[i];
            ImGui.PushID($"branch-{i}-{b.Id}");

            var label = string.IsNullOrEmpty(b.Label) ? b.Id : b.Label;
            ImGui.BulletText(label);
            ImGui.Indent();
            ImGui.TextDisabled($"id: {b.Id}");
            ImGui.TextDisabled($"判定: {b.Condition?.Type} cast_id={b.Condition?.CastId} 名前={b.Condition?.CastName} window={b.Condition?.WindowSec:0}s");
            // 紐付き mechanic 数
            var profile = StrategyPlanResolver.SelectActiveProfile(_workingCopy);
            var attached = profile?.Mechanics.Count(m => m.BranchId == b.Id) ?? 0;
            ImGui.TextDisabled($"紐付きメカニクス: {attached} 件");
            if (ImGui.SmallButton($"この分岐を削除"))
            {
                _workingCopy.Branches.RemoveAt(i);
                // 紐付いていた mechanic の branch_id をクリア（孤児防止）
                if (profile is not null)
                {
                    foreach (var m in profile.Mechanics)
                    {
                        if (m.BranchId == b.Id) m.BranchId = null;
                    }
                }
                _dirty = true;
                ImGui.Unindent();
                ImGui.PopID();
                break;
            }
            ImGui.Unindent();
            ImGui.PopID();
        }
        ImGui.Unindent();
    }

    private void GenerateStrategyDraftsFromRecording(StrategyProfile profile)
    {
        if (_workingCopy is null)
        {
            return;
        }

        var agg = _recordingScanner.Aggregate(_workingZone);
        var party = new HashSet<string>(
            _recordingScanner.ListPartyMembers(_workingZone),
            StringComparer.OrdinalIgnoreCase);
        var result = StrategyDraftGenerator.Generate(agg, profile, party);
        foreach (var mechanic in result.Generated)
        {
            profile.Mechanics.Add(mechanic);
        }

        _dirty |= result.Generated.Count > 0;
        _lastStrategyDraftMessage =
            $"追加 {result.Generated.Count} 件 / スキップ {result.Skipped.Count} 件";
    }

    private void DrawStrategyProfileSelector()
    {
        if (_workingCopy is null)
        {
            return;
        }

        var profiles = _workingCopy.StrategyProfiles;
        var labels = profiles
            .Select(p => string.IsNullOrWhiteSpace(p.Name) ? p.Id : $"{p.Name} ({p.Id})")
            .ToArray();
        var selectedIndex = Math.Max(0, profiles.FindIndex(p =>
            string.Equals(p.Id, _workingCopy.ActiveStrategyProfileId, StringComparison.OrdinalIgnoreCase)));

        ImGui.SetNextItemWidth(360f * ImGuiHelpers.GlobalScale);
        if (ImGui.Combo("使用中のプロファイル", ref selectedIndex, labels, labels.Length))
        {
            _workingCopy.ActiveStrategyProfileId = profiles[selectedIndex].Id;
            _dirty = true;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("戦闘中に StrategyPlanResolver がここで選択中のプロファイルだけを使う。\n" +
                             "固定 PT 用 / 野良用 / 練習用 等を切り替えるための機構。");
        }

        ImGui.SameLine();
        if (ImGui.Button("プロファイル追加"))
        {
            var id = UniqueStrategyId("profile");
            var profile = CreateDefaultStrategyProfile(id, $"攻略 {profiles.Count + 1}");
            profiles.Add(profile);
            _workingCopy.ActiveStrategyProfileId = profile.Id;
            _dirty = true;
        }
    }

    private void DrawStrategyProfileEditor(StrategyProfile profile)
    {
        ImGui.TextUnformatted("プロファイル基本情報");
        var enabled = profile.Enabled;
        if (ImGui.Checkbox("有効##strategy-profile-enabled", ref enabled))
        {
            profile.Enabled = enabled;
            _dirty = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("OFF にするとこのプロファイルは戦闘中に使われない");

        var id = profile.Id;
        ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("ID（内部識別子）##strategy-profile-id", ref id, 64))
        {
            var oldId = profile.Id;
            profile.Id = string.IsNullOrWhiteSpace(id) ? oldId : id.Trim();
            if (_workingCopy?.ActiveStrategyProfileId == oldId)
            {
                _workingCopy.ActiveStrategyProfileId = profile.Id;
            }
            _dirty = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("英数字推奨。プロファイル切替やトリガー連携で参照される");

        var name = profile.Name;
        ImGui.SetNextItemWidth(320f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("表示名##strategy-profile-name", ref name, 128))
        {
            profile.Name = name;
            _dirty = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("UI で表示される名前（例：「固定 PT 用」「野良用」）");

        DrawArenaShapeAndCalibration(profile);
        DrawObjectAoeRuleEditor(profile);

        var description = profile.Description ?? string.Empty;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextMultiline("メモ##strategy-description", ref description, 1024,
                new Vector2(-1, 60f * ImGuiHelpers.GlobalScale)))
        {
            profile.Description = string.IsNullOrWhiteSpace(description) ? null : description;
            _dirty = true;
        }
    }

    private void DrawObjectAoeRuleEditor(StrategyProfile profile)
    {
        ImGui.Spacing();
        if (!ImGui.CollapsingHeader($"オブジェクトAoEルール（ランダムギミック）##obj-aoe-rules-{profile.Id}",
                ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        profile.ObjectAoeRules ??= new List<ObjectAoeRule>();
        ImGui.TextWrapped(
            "パラデイグマ系は、出現位置ではなくオブジェクト名/DataIdごとに範囲形状を登録する。戦闘中は実際に出た位置へ自動で範囲を置く。");

        if (ImGui.SmallButton($"ルール追加##obj-aoe-add-{profile.Id}"))
        {
            profile.ObjectAoeRules.Add(new ObjectAoeRule
            {
                Id = ObjectAoeRuleResolver.MakeRuleId(0, $"rule{profile.ObjectAoeRules.Count + 1}"),
                ObjectName = string.Empty,
                NameMatch = "contains",
                Source = "manual",
                Shape = "line",
                RadiusM = Math.Max(40.0, (profile.ArenaRadius ?? 20.0) * 2.0),
                HalfWidthM = AoeGeometryPolicy.DefaultLineHalfWidthM,
                Color = "#FF6464",
                LiveFloorPaint = true,
            });
            _dirty = true;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("キャストIDではなく、出現するオブジェクト名や DataId で範囲形状を登録する。");
        }

        if (profile.ObjectAoeRules.Count == 0)
        {
            ImGui.TextDisabled("まだ登録なし。録画から学習されたもの、または手動追加したものがここに表示されます。");
            return;
        }

        if (!ImGui.BeginTable($"##object-aoe-rules-table-{profile.Id}", 11,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }

        ImGui.TableSetupColumn("有効", ImGuiTableColumnFlags.WidthFixed, 52f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("オブジェクト名", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("DataId", ImGuiTableColumnFlags.WidthFixed, 90f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("一致", ImGuiTableColumnFlags.WidthFixed, 92f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("形", ImGuiTableColumnFlags.WidthFixed, 104f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("半径/内径", ImGuiTableColumnFlags.WidthFixed, 120f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("扇角/半幅", ImGuiTableColumnFlags.WidthFixed, 112f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("秒", ImGuiTableColumnFlags.WidthFixed, 60f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("床", ImGuiTableColumnFlags.WidthFixed, 44f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("元", ImGuiTableColumnFlags.WidthFixed, 86f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 58f * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        var nameMatchLabels = new[] { "含む", "完全一致", "前方一致" };
        var nameMatchValues = new[] { "contains", "exact", "startswith" };
        var shapeLabels = new[] { "円", "ドーナツ", "扇形", "矩形", "直線", "半面", "十字", "扇ドーナツ" };
        var shapeValues = new[] { "circle", "donut", "cone", "rect", "line", "half_plane", "cross", "donut_cone" };

        for (var i = 0; i < profile.ObjectAoeRules.Count; i++)
        {
            var rule = profile.ObjectAoeRules[i];
            ImGui.PushID($"object-aoe-rule-{profile.Id}-{i}");
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var enabled = rule.Enabled;
            if (ImGui.Checkbox("##enabled", ref enabled))
            {
                rule.Enabled = enabled;
                _dirty = true;
            }

            ImGui.TableNextColumn();
            var objectName = rule.ObjectName ?? string.Empty;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##object-name", ref objectName, 128))
            {
                rule.ObjectName = string.IsNullOrWhiteSpace(objectName) ? null : objectName.Trim();
                if (string.IsNullOrWhiteSpace(rule.Id))
                {
                    rule.Id = ObjectAoeRuleResolver.MakeRuleId(rule.DataId ?? 0, rule.ObjectName);
                }
                _dirty = true;
            }

            ImGui.TableNextColumn();
            var dataIdText = rule.DataId is { } dataId ? dataId.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##data-id", ref dataIdText, 24))
            {
                if (string.IsNullOrWhiteSpace(dataIdText))
                {
                    rule.DataId = null;
                    _dirty = true;
                }
                else if (TryParseUint(dataIdText, out var parsedDataId))
                {
                    rule.DataId = parsedDataId == 0 ? null : parsedDataId;
                    _dirty = true;
                }
            }

            ImGui.TableNextColumn();
            var nameMatchIdx = Array.FindIndex(nameMatchValues,
                value => string.Equals(value, rule.NameMatch, StringComparison.OrdinalIgnoreCase));
            if (nameMatchIdx < 0) nameMatchIdx = 0;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo("##name-match", ref nameMatchIdx, nameMatchLabels, nameMatchLabels.Length))
            {
                rule.NameMatch = nameMatchValues[nameMatchIdx];
                _dirty = true;
            }

            ImGui.TableNextColumn();
            var shapeIdx = Array.FindIndex(shapeValues,
                value => string.Equals(value, rule.Shape, StringComparison.OrdinalIgnoreCase));
            if (shapeIdx < 0) shapeIdx = 0;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo("##shape", ref shapeIdx, shapeLabels, shapeLabels.Length))
            {
                rule.Shape = shapeValues[shapeIdx];
                ApplyObjectAoeShapeDefaults(rule, profile);
                _dirty = true;
            }

            ImGui.TableNextColumn();
            var radius = (float)Math.Max(0.5, rule.RadiusM);
            ImGui.SetNextItemWidth(58f * ImGuiHelpers.GlobalScale);
            if (ImGui.InputFloat("R##radius", ref radius, 0.5f, 1f, "%.1f"))
            {
                rule.RadiusM = Math.Max(0.5, radius);
                _dirty = true;
            }
            if (rule.Shape is "donut" or "donut_cone")
            {
                ImGui.SameLine();
                var inner = (float)(rule.InnerRadiusM ?? rule.RadiusM * 0.30);
                ImGui.SetNextItemWidth(58f * ImGuiHelpers.GlobalScale);
                if (ImGui.InputFloat("I##inner", ref inner, 0.5f, 1f, "%.1f"))
                {
                    rule.InnerRadiusM = Math.Max(0, inner);
                    _dirty = true;
                }
            }

            ImGui.TableNextColumn();
            if (rule.Shape is "cone" or "donut_cone")
            {
                var fan = (float)(rule.FanDeg ?? 90);
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputFloat("扇##fan", ref fan, 5f, 15f, "%.0f"))
                {
                    rule.FanDeg = Math.Clamp(fan, 1f, 360f);
                    _dirty = true;
                }
            }
            else if (rule.Shape is "rect" or "line" or "half_plane")
            {
                var halfWidth = (float)(rule.HalfWidthM ?? AoeGeometryPolicy.DefaultLineHalfWidthM);
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputFloat("幅##half-width", ref halfWidth, 0.5f, 1f, "%.1f"))
                {
                    rule.HalfWidthM = Math.Max(0.5, halfWidth);
                    _dirty = true;
                }
            }
            else
            {
                ImGui.TextDisabled("-");
            }

            ImGui.TableNextColumn();
            var duration = (float)(rule.DurationSec ?? 10.0);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputFloat("##duration", ref duration, 0.5f, 1f, "%.1f"))
            {
                rule.DurationSec = duration <= 0 ? null : duration;
                _dirty = true;
            }

            ImGui.TableNextColumn();
            var floor = rule.LiveFloorPaint;
            if (ImGui.Checkbox("##floor", ref floor))
            {
                rule.LiveFloorPaint = floor;
                _dirty = true;
            }

            ImGui.TableNextColumn();
            ImGui.TextDisabled(SourceLabel(rule.Source));

            ImGui.TableNextColumn();
            if (ImGui.SmallButton("削除"))
            {
                profile.ObjectAoeRules.RemoveAt(i);
                _dirty = true;
                ImGui.PopID();
                break;
            }

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private static void ApplyObjectAoeShapeDefaults(ObjectAoeRule rule, StrategyProfile profile)
    {
        var arenaRadius = profile.ArenaRadius ?? Math.Max(profile.ArenaWidth ?? 40.0, profile.ArenaDepth ?? 40.0) * 0.5;
        rule.Shape = ObjectAoeRuleResolver.NormalizeShape(rule.Shape);
        if (rule.Shape is "line" or "rect")
        {
            rule.RadiusM = Math.Max(rule.RadiusM, arenaRadius * 2.0);
            rule.HalfWidthM ??= AoeGeometryPolicy.DefaultLineHalfWidthM;
        }
        else if (rule.Shape == "half_plane")
        {
            rule.RadiusM = Math.Max(rule.RadiusM, arenaRadius * 2.0);
            rule.HalfWidthM ??= arenaRadius;
        }
        else if (rule.Shape is "cone" or "donut_cone")
        {
            rule.FanDeg ??= 90.0;
        }
        else if (rule.Shape == "donut")
        {
            rule.InnerRadiusM ??= Math.Max(0.5, rule.RadiusM * 0.30);
        }
    }

    private static bool TryParseUint(string text, out uint value)
    {
        value = 0;
        var trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(
                trimmed[2..],
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out value);
        }

        return uint.TryParse(
            trimmed,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out value);
    }

    private static string SourceLabel(string? source)
    {
        return (source ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "manual" => "手動",
            "recording" => "録画学習",
            "dictionary" => "辞書",
            "builtin" => "内蔵",
            _ => string.IsNullOrWhiteSpace(source) ? "-" : source!,
        };
    }

    private void DrawStrategyPositionsEditor(StrategyProfile profile)
    {
        ImGui.TextUnformatted("散開ポジション（8 人分の立ち位置）");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "散開ギミックの「自分はここに立つ」の座標一覧。\n" +
                "アリーナ中央を (0, 0) として、X = 東(+) / 西(-)、Z = 南(+) / 北(-)。\n" +
                "ミニマップに重ねて表示したり、メカニクスの positions で参照したりする。");
        }
        ImGui.SameLine(0, 24f * ImGuiHelpers.GlobalScale);
        if (_profileCenterDragMode)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.7f, 0.4f, 0.2f, 1f));
        }
        if (ImGui.SmallButton($"{(_profileCenterDragMode ? "■" : "○")} 中心調整##profile-center-drag-mode-map"))
        {
            _profileCenterDragMode = !_profileCenterDragMode;
            _centerClickModeMechId = null;
        }
        if (_profileCenterDragMode)
        {
            ImGui.PopStyleColor();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "ON 中はこのマップ上でプロファイル既定のアリーナ中心を調整します。\n" +
                "ドラッグで微調整、空白ダブルクリックでその地点を中心に寄せます。");
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("ポジ追加"))
        {
            profile.SpreadPositions.Add(new StrategyPosition
            {
                Slot = $"P{profile.SpreadPositions.Count + 1}",
                Label = $"P{profile.SpreadPositions.Count + 1}",
                Color = "#F472B6",
            });
            _dirty = true;
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("8 方向ポジを補完"))
        {
            var (halfW, halfD) = ResolveArenaHalfExtents(profile);
            foreach (var pos in CreateEightWaySpreadPositions(halfW, halfD))
            {
                if (profile.SpreadPositions.Any(p => string.Equals(p.Slot, pos.Slot, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                profile.SpreadPositions.Add(pos);
            }
            _dirty = true;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("MT/ST/H1/H2/D1-D4 の標準 8 方向散開ポジを自動追加（既存 slot は保護）");
        }

        // 視覚的にドラッグ＆ドロップで配置できるミニマップエディタ
        ImGui.Spacing();
        DrawSpreadPositionMapEditor(profile);
        ImGui.Spacing();
        if (_profileCenterDragMode)
        {
            ImGui.TextColored(new Vector4(1f, 0.75f, 0.35f, 1f),
                "中心調整ON: マップをドラッグ / 空白ダブルクリックでアリーナ中心だけを移動。PT配置は動きません。");
        }
        else
        {
            ImGui.TextDisabled("↑ ドット をドラッグで移動。空白部分をダブルクリックでポジ追加。下の表で詳細編集。");
        }
        ImGui.Spacing();

        if (!ImGui.BeginTable("##strategy-positions-table", 7,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable))
        {
            return;
        }

        ImGui.TableSetupColumn("スロット", ImGuiTableColumnFlags.WidthFixed, 64f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("表示名", ImGuiTableColumnFlags.WidthFixed, 70f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("ロール", ImGuiTableColumnFlags.WidthFixed, 82f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("ジョブ", ImGuiTableColumnFlags.WidthFixed, 56f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("色", ImGuiTableColumnFlags.WidthFixed, 88f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("メモ", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 68f * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        for (var i = 0; i < profile.SpreadPositions.Count; i++)
        {
            var pos = profile.SpreadPositions[i];
            ImGui.PushID($"strategy-pos-{i}");
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var slot = pos.Slot;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##slot", ref slot, 32))
            {
                pos.Slot = slot;
                _dirty = true;
            }

            ImGui.TableNextColumn();
            var label = pos.Label ?? string.Empty;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##label", ref label, 32))
            {
                pos.Label = string.IsNullOrWhiteSpace(label) ? null : label;
                _dirty = true;
            }

            ImGui.TableNextColumn();
            var role = pos.Role ?? string.Empty;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##role", ref role, 32))
            {
                pos.Role = string.IsNullOrWhiteSpace(role) ? null : role;
                _dirty = true;
            }

            ImGui.TableNextColumn();
            var job = pos.Job ?? string.Empty;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##job", ref job, 16))
            {
                pos.Job = string.IsNullOrWhiteSpace(job) ? null : job;
                _dirty = true;
            }

            ImGui.TableNextColumn();
            var color = pos.Color ?? string.Empty;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##color", ref color, 16))
            {
                pos.Color = string.IsNullOrWhiteSpace(color) ? null : color;
                _dirty = true;
            }

            ImGui.TableNextColumn();
            var note = pos.Note ?? string.Empty;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##note", ref note, 128))
            {
                pos.Note = string.IsNullOrWhiteSpace(note) ? null : note;
                _dirty = true;
            }

            ImGui.TableNextColumn();
            // 現在座標をホバーで確認できるようにする（座標自体はドラッグで編集）
            ImGui.TextDisabled($"({pos.X:0.0}, {pos.Z:0.0})m");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("位置はドット をドラッグして変更");
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("削除"))
            {
                profile.SpreadPositions.RemoveAt(i);
                _dirty = true;
                ImGui.PopID();
                break;
            }

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void DrawMechanicStrategiesEditor(StrategyProfile profile)
    {
        ImGui.TextUnformatted("ギミック攻略");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "ボスのギミックごとの処理プラン。集計タブで「攻略ギミックに追加」を押すと\n" +
                "ここに自動でエントリが作られる。各メカニクスに：\n" +
                "・通知タイミング（何秒前に音声を流すか）\n" +
                "・読み上げ文（callout）\n" +
                "・担当ロール / ジョブ\n" +
                "・ミニマップ表示形状\n" +
                "・散開ポジ参照（positions）\n" +
                "・安置計算（safe_zone）\n" +
                "を設定する。最低限 callout と先行通知秒数だけでも動く。");
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("メカニクス追加"))
        {
            var id = UniqueMechanicId(profile, "mechanic");
            profile.Mechanics.Add(new MechanicStrategy
            {
                Id = id,
                Label = $"メカニクス {profile.Mechanics.Count + 1}",
                Time = 0,
                AdvanceWarningSec = 5,
                Color = "#F472B6",
                Phase = string.IsNullOrWhiteSpace(_targetPhaseForNewMechanics) ? null : _targetPhaseForNewMechanics,
            });
            _dirty = true;
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("フェーズ追加"))
        {
            _newPhaseProfileId = profile.Id;
            var auto = $"P{Math.Max(1, profile.Mechanics.Select(m => m.Phase).Distinct().Count() + 1)}";
            _newPhaseName = auto;
            _newPhaseFirstMechanicLabel = "ギミック 1";
            ImGui.OpenPopup("##phase-create-popup");
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(
            "フェーズ単位（P1 / P2 / 終焉 等）でメカニクスをグルーピングする。\n" +
            "ボタンを押すとフェーズ名と最初のメカニクスを聞かれる。");

        ImGui.SameLine();
        if (ImGui.SmallButton("テンプレートから作成"))
        {
            ImGui.OpenPopup("##template-picker");
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(
            "よくあるギミック（散開・ドーナツ回避・タワー等）のひな形を選んで追加する。\n" +
            "追加後にラベル・発動条件・ポジションを微調整するだけで使える。");

        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.55f, 0.20f, 0.20f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.70f, 0.30f, 0.30f, 1f));
        if (ImGui.SmallButton("全メカニクスをクリア"))
        {
            _shouldOpenClearAllMechanics = true;
            _clearAllProfileId = profile.Id;
        }
        ImGui.PopStyleColor(2);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(
            "このプロファイルの全フェーズ・全メカニクスを削除する。\n" +
            "「録画削除して最初からやり直したい」時に使う。");

        if (ImGui.BeginPopup("##template-picker"))
        {
            ImGui.TextDisabled("追加したいテンプレートを選択");
            ImGui.Separator();
            foreach (var entry in MechanicTemplates.All)
            {
                ImGui.PushID($"tpl-{entry.Id}");
                ImGui.TextUnformatted(entry.Label);
                ImGui.SameLine();
                if (ImGui.SmallButton("適用##tpl-apply"))
                {
                    var mechanic = entry.Build();
                    mechanic.Id = UniqueMechanicId(profile, entry.Id);
                    if (!string.IsNullOrWhiteSpace(_targetPhaseForNewMechanics))
                    {
                        mechanic.Phase = _targetPhaseForNewMechanics;
                    }
                    // テンプレートは半径 20m のアリーナ前提で書かれているので、
                    // 実プロファイル寸法に合わせて散開ポジ／オブジェクト／AoE 中心をスケール。
                    var (halfW, halfD) = ResolveArenaHalfExtents(profile);
                    MechanicTemplates.RescaleToArena(mechanic, halfW, halfD);
                    // テンプレ生成 mechanic にプロファイル形状を明示的に継承させる：
                    // 「正方形のプロファイルなのにテンプレ適用したら円形マップ」みたいな
                    // cascade fallback 由来の事故が起きないようにする。
                    mechanic.ArenaShape ??= profile.ArenaShape;
                    mechanic.ArenaRadius ??= profile.ArenaRadius;
                    mechanic.ArenaWidth ??= profile.ArenaWidth;
                    mechanic.ArenaDepth ??= profile.ArenaDepth;
                    mechanic.ArenaCenterX ??= profile.ArenaCenterX;
                    mechanic.ArenaCenterZ ??= profile.ArenaCenterZ;
                    profile.Mechanics.Add(mechanic);
                    _dirty = true;
                    ImGui.CloseCurrentPopup();
                    ImGui.PopID();
                    break;
                }
                ImGui.TextDisabled($"  {entry.Description}");
                ImGui.PopID();
            }
            ImGui.EndPopup();
        }

        // 新規メカニクスの追加先フェーズ（メカニクス追加 / テンプレート適用が使う）
        ImGui.Spacing();
        ImGui.TextUnformatted("新規メカニクスのフェーズ:");
        ImGui.SameLine();
        var existingPhasesAll = profile.Mechanics
            .Select(m => m.Phase)
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var phaseOptions = new[] { "（未分類）" }.Concat(existingPhasesAll).Concat(new[] { "新規..." }).ToArray();
        var phaseOptionKeys = new[] { (string?)null }
            .Concat(existingPhasesAll.Select(p => (string?)p))
            .Concat(new[] { (string?)"NEW" })
            .ToArray();
        var phaseIdx = 0;
        for (var k = 1; k < phaseOptionKeys.Length; k++)
        {
            if (string.Equals(phaseOptionKeys[k], _targetPhaseForNewMechanics, StringComparison.OrdinalIgnoreCase))
            {
                phaseIdx = k;
                break;
            }
        }
        ImGui.SetNextItemWidth(160f * ImGuiHelpers.GlobalScale);
        if (ImGui.Combo("##new-mech-phase", ref phaseIdx, phaseOptions, phaseOptions.Length))
        {
            var picked = phaseOptionKeys[phaseIdx];
            if (picked == "NEW")
            {
                _newPhaseProfileId = profile.Id;
                var auto = $"P{Math.Max(1, existingPhasesAll.Length + 1)}";
                _newPhaseName = auto;
                _newPhaseFirstMechanicLabel = "ギミック 1";
                ImGui.OpenPopup("##phase-create-popup");
            }
            else
            {
                _targetPhaseForNewMechanics = picked;
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("「メカニクス追加」「テンプレートから作成」したメカニクスがどのフェーズに入るか。\n" +
                              "現在の選択が以降の追加に適用される。");
        }

        if (profile.Mechanics.Count == 0)
        {
            ImGui.TextDisabled("「集計（観測イベント）」タブで cast を選択 → 「攻略ギミックに追加」で作成、" +
                                "または上の「メカニクス追加」で手動作成。");
            return;
        }

        // フェーズでグルーピング表示。Phase 未設定は「（未分類）」グループ。
        var groups = profile.Mechanics
            .Select((m, idx) => (m, idx))
            .GroupBy(t => string.IsNullOrEmpty(t.m.Phase) ? "（未分類）" : t.m.Phase!)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var grp in groups)
        {
            var groupKey = grp.Key;
            var groupItems = grp.ToArray();
            // 重要：phase ベースの PushID は使わない。フェーズ名を 1 文字編集するたびに
            // groupKey が変わり、ID stack 全体が変化、配下の InputText がフォーカスを
            // 失うバグの元になる。各メカニクスの PushID（mechanic の元 index 由来）で
            // 既に一意性は保証されているので、外側の PushID は不要。

            // フェーズ見出し行：折りたたみヘッダ + rename / 削除ボタン
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.4f, 0.85f, 1f, 1f));
            var open = ImGui.CollapsingHeader($"■ フェーズ：{groupKey}（{groupItems.Length} 件）",
                ImGuiTreeNodeFlags.DefaultOpen);
            ImGui.PopStyleColor();

            // ヘッダ右にインライン編集 UI（rename / 全削除）。SameLine() でヘッダの右に並べる
            ImGui.SameLine();
            if (ImGui.SmallButton($"名前変更##phase-rename-{groupKey}"))
            {
                _phaseRenameOldKey = groupKey;
                _phaseRenameNewKey = string.Equals(groupKey, "（未分類）") ? "" : groupKey;
                _phaseRenameProfileId = profile.Id;
                _shouldOpenPhaseRename = true;
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("このグループ内の全メカニクスのフェーズ名をまとめて変更");
            ImGui.SameLine();
            if (ImGui.SmallButton($"全メカニクス削除##phase-del-{groupKey}"))
            {
                _phaseDeleteKey = groupKey;
                _phaseDeleteProfileId = profile.Id;
                _shouldOpenPhaseDelete = true;
            }
            // フェーズ単位の形状設定（フェーズで地形が変わるケース：P1=円 / P2=矩形 等）
            if (!string.Equals(groupKey, "（未分類）"))
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"形状設定##phase-shape-{groupKey}"))
                {
                    _phaseShapeKey = groupKey;
                    _phaseShapeProfileId = profile.Id;
                    _shouldOpenPhaseShape = true;
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(
                    "このフェーズだけのアリーナ形状・寸法・中心座標を設定する。\n" +
                    "未指定ならプロファイル既定を継承。\n" +
                    "P1 が円形・P2 が正方形などフェーズで地形が変わる場合に使う。");
            }

            if (open)
            {
                ImGui.Indent();
                foreach (var (mechanic, i) in groupItems)
                {
                    ImGui.PushID($"strategy-mechanic-{i}");
                    var title = string.IsNullOrWhiteSpace(mechanic.Label) ? mechanic.Id : mechanic.Label;
                    if (ImGui.CollapsingHeader($"{title}##strategy-mechanic-header", ImGuiTreeNodeFlags.DefaultOpen))
                    {
                        DrawMechanicStrategyEditor(profile, mechanic, i);
                    }
                    ImGui.PopID();
                }
                ImGui.Unindent();
            }
        }

        DrawPhaseRenamePopup(profile);
        DrawPhaseDeletePopup(profile);
        DrawPhaseCreatePopup(profile);
        DrawPhaseShapePopup(profile);
        DrawClearAllMechanicsPopup(profile);
    }

    private void DrawClearAllMechanicsPopup(StrategyProfile profile)
    {
        if (_shouldOpenClearAllMechanics)
        {
            _shouldOpenClearAllMechanics = false;
            ImGui.OpenPopup("##clear-all-mechanics-popup");
        }
        if (profile.Id != _clearAllProfileId) return;
        ImGui.SetNextWindowSize(new Vector2(420f * ImGuiHelpers.GlobalScale, 0f),
            ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal("##clear-all-mechanics-popup",
            ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.TextWrapped(
            $"プロファイル「{profile.Name}」の全メカニクス（{profile.Mechanics.Count} 件）と" +
            $"フェーズ既定（{profile.PhaseArenaShapes.Count} 件）を削除します。\n" +
            "この操作は元に戻せません。よろしいですか？");
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.55f, 0.20f, 0.20f, 1f));
        if (ImGui.Button("全削除する", new Vector2(140f * ImGuiHelpers.GlobalScale, 0f)))
        {
            profile.Mechanics.Clear();
            profile.PhaseArenaShapes.Clear();
            _dirty = true;
            _clearAllProfileId = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.PopStyleColor();
        ImGui.SameLine();
        if (ImGui.Button("キャンセル", new Vector2(120f * ImGuiHelpers.GlobalScale, 0f)))
        {
            _clearAllProfileId = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private string? _phaseShapeKey;
    private string? _phaseShapeProfileId;
    private bool _shouldOpenPhaseShape;
    /// <summary>「名前変更」ボタン押下フラグ。ループ内で OpenPopup を呼ぶと ID stack
    /// が CollapsingHeader 配下になり、ループ外の BeginPopupModal と一致しない。
    /// 同じ階層から OpenPopup を呼ぶため、フラグだけ立ててポップアップ関数側で OpenPopup する。</summary>
    private bool _shouldOpenPhaseRename;
    private bool _shouldOpenPhaseDelete;
    private bool _shouldOpenClearAllMechanics;
    private string? _clearAllProfileId;

    private void DrawPhaseShapePopup(StrategyProfile profile)
    {
        if (_shouldOpenPhaseShape)
        {
            _shouldOpenPhaseShape = false;
            ImGui.OpenPopup("##phase-shape-popup");
        }
        if (profile.Id != _phaseShapeProfileId) return;
        if (string.IsNullOrEmpty(_phaseShapeKey)) return;

        ImGui.SetNextWindowSize(new Vector2(440f * ImGuiHelpers.GlobalScale, 0f),
            ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal("##phase-shape-popup", ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.TextWrapped($"フェーズ「{_phaseShapeKey}」専用のアリーナ形状・寸法を設定します。\n" +
                            "（未指定の項目はプロファイル既定を継承）");
        ImGui.Spacing();

        if (!profile.PhaseArenaShapes.TryGetValue(_phaseShapeKey!, out var spec))
        {
            spec = new PhaseArenaSpec();
            profile.PhaseArenaShapes[_phaseShapeKey!] = spec;
        }

        // 形状コンボ
        var shapeKeys = new[] { (string?)null, "circle", "square", "rect" };
        var shapeLabels = new[] { "（プロファイル継承）", "円形", "正方形", "長方形" };
        var idx = 0;
        for (var k = 1; k < shapeKeys.Length; k++)
        {
            if (string.Equals(shapeKeys[k], spec.Shape, StringComparison.OrdinalIgnoreCase))
            {
                idx = k;
                break;
            }
        }
        ImGui.SetNextItemWidth(200f * ImGuiHelpers.GlobalScale);
        if (ImGui.Combo("形状##ps-shape", ref idx, shapeLabels, shapeLabels.Length))
        {
            spec.Shape = shapeKeys[idx];
            _dirty = true;
        }

        var effective = spec.Shape ?? profile.ArenaShape ?? "circle";
        if (string.Equals(effective, "circle", StringComparison.OrdinalIgnoreCase))
        {
            var rad = (float)(spec.Radius ?? profile.ArenaRadius ?? 20.0);
            ImGui.SetNextItemWidth(140f * ImGuiHelpers.GlobalScale);
            if (ImGui.InputFloat("半径(m)##ps-rad", ref rad, 0.5f, 1f, "%.1f"))
            {
                spec.Radius = rad <= 0 ? null : rad;
                _dirty = true;
            }
        }
        else
        {
            var w = (float)(spec.Width ?? profile.ArenaWidth ?? (profile.ArenaRadius ?? 20.0) * 2);
            var d = (float)(spec.Depth ?? profile.ArenaDepth ?? (profile.ArenaRadius ?? 20.0) * 2);
            ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
            if (ImGui.InputFloat("幅(東西m)##ps-w", ref w, 0.5f, 1f, "%.1f"))
            {
                spec.Width = w <= 0 ? null : w;
                _dirty = true;
            }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
            if (ImGui.InputFloat("奥行(南北m)##ps-d", ref d, 0.5f, 1f, "%.1f"))
            {
                spec.Depth = d <= 0 ? null : d;
                _dirty = true;
            }
        }

        ImGui.Spacing();
        ImGui.TextDisabled("中心座標（戦闘中のアリーナ中心ワールド座標）");
        var hasCenter = spec.CenterX.HasValue && spec.CenterZ.HasValue;
        if (hasCenter)
        {
            ImGui.TextDisabled($"現在: ({spec.CenterX:0.0}, {spec.CenterZ:0.0})");
        }
        else
        {
            ImGui.TextDisabled("現在: 未設定（プロファイル中心 or 自動推定を使用）");
        }
        if (ImGui.Button("ここを中心にする##ps-here"))
        {
            if (_objectTable?.LocalPlayer is { } self)
            {
                spec.CenterX = self.Position.X;
                spec.CenterZ = self.Position.Z;
                _dirty = true;
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("中心をクリア##ps-clear-center"))
        {
            spec.CenterX = null;
            spec.CenterZ = null;
            _dirty = true;
        }

        ImGui.Spacing();
        if (ImGui.Button("閉じる##ps-close", new Vector2(120f * ImGuiHelpers.GlobalScale, 0f)))
        {
            // 全フィールドが null ならエントリ自体削除
            if (spec.Shape is null && spec.Radius is null && spec.Width is null
                && spec.Depth is null && spec.CenterX is null && spec.CenterZ is null)
            {
                profile.PhaseArenaShapes.Remove(_phaseShapeKey!);
            }
            _phaseShapeKey = null;
            _phaseShapeProfileId = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private string? _phaseRenameOldKey;
    private string _phaseRenameNewKey = "";
    private string? _phaseRenameProfileId;
    private string? _phaseDeleteKey;
    private string? _phaseDeleteProfileId;
    private string? _newPhaseProfileId;
    private string _newPhaseName = "";
    private string _newPhaseFirstMechanicLabel = "";
    private string? _targetPhaseForNewMechanics;
    private string? _movePhaseProfileId;
    private string? _movePhaseMechanicId;
    private string _movePhaseNewName = "";

    private void DrawMovePhaseNewPopup(StrategyProfile profile)
    {
        if (profile.Id != _movePhaseProfileId) return;
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(380f * ImGuiHelpers.GlobalScale, 0f));
        if (!ImGui.BeginPopupModal("##move-phase-new-popup", ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }
        ImGui.TextWrapped("移動先の新規フェーズ名：");
        ImGui.SetNextItemWidth(-1);
        var name = _movePhaseNewName;
        ImGui.InputText("##move-phase-new-name", ref name, 64);
        _movePhaseNewName = name;
        ImGui.Spacing();
        if (ImGui.Button("移動", new System.Numerics.Vector2(120f * ImGuiHelpers.GlobalScale, 0f)))
        {
            var target = profile.Mechanics.FirstOrDefault(m => m.Id == _movePhaseMechanicId);
            if (target is not null)
            {
                target.Phase = string.IsNullOrWhiteSpace(_movePhaseNewName) ? null : _movePhaseNewName.Trim();
                _dirty = true;
            }
            _movePhaseProfileId = null;
            _movePhaseMechanicId = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("キャンセル", new System.Numerics.Vector2(120f * ImGuiHelpers.GlobalScale, 0f)))
        {
            _movePhaseProfileId = null;
            _movePhaseMechanicId = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void DrawPhaseCreatePopup(StrategyProfile profile)
    {
        if (profile.Id != _newPhaseProfileId) return;
        ImGui.SetNextWindowSize(new Vector2(400f * ImGuiHelpers.GlobalScale, 0f));
        if (!ImGui.BeginPopupModal("##phase-create-popup", ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.TextWrapped("新しいフェーズを作成します。フェーズ名と最初のギミック名を入力してください。");
        ImGui.Spacing();

        var phase = _newPhaseName;
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("フェーズ名 (例：P2 / 終焉)##new-phase-name", ref phase, 64);
        _newPhaseName = phase;

        var label = _newPhaseFirstMechanicLabel;
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("最初のギミック名##new-phase-mech", ref label, 64);
        _newPhaseFirstMechanicLabel = label;

        ImGui.Spacing();
        var canCreate = !string.IsNullOrWhiteSpace(_newPhaseName);
        if (!canCreate) ImGui.BeginDisabled();
        if (ImGui.Button("作成", new Vector2(120f * ImGuiHelpers.GlobalScale, 0f)))
        {
            var phaseTrim = _newPhaseName.Trim();
            var labelTrim = string.IsNullOrWhiteSpace(_newPhaseFirstMechanicLabel)
                ? "ギミック 1"
                : _newPhaseFirstMechanicLabel.Trim();
            profile.Mechanics.Add(new MechanicStrategy
            {
                Id = UniqueMechanicId(profile, phaseTrim.ToLowerInvariant()),
                Label = labelTrim,
                Phase = phaseTrim,
                AdvanceWarningSec = 5,
                Color = "#F472B6",
            });
            _dirty = true;
            _newPhaseProfileId = null;
            ImGui.CloseCurrentPopup();
        }
        if (!canCreate) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("キャンセル", new Vector2(120f * ImGuiHelpers.GlobalScale, 0f)))
        {
            _newPhaseProfileId = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void DrawPhaseRenamePopup(StrategyProfile profile)
    {
        if (_shouldOpenPhaseRename && profile.Id == _phaseRenameProfileId)
        {
            _shouldOpenPhaseRename = false;
            ImGui.OpenPopup("##phase-rename-popup");
        }
        if (profile.Id != _phaseRenameProfileId) return;
        ImGui.SetNextWindowSize(new Vector2(380f * ImGuiHelpers.GlobalScale, 0f));
        if (!ImGui.BeginPopupModal("##phase-rename-popup", ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.TextWrapped(string.Equals(_phaseRenameOldKey, "（未分類）")
            ? "未分類のメカニクスにフェーズ名を付ける（空欄なら未分類のまま）："
            : $"フェーズ「{_phaseRenameOldKey}」の新しい名前：");
        ImGui.Spacing();
        var name = _phaseRenameNewKey;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##phase-rename-input", ref name, 64))
        {
            _phaseRenameNewKey = name;
        }
        ImGui.Spacing();
        if (ImGui.Button("適用", new Vector2(120f * ImGuiHelpers.GlobalScale, 0f)))
        {
            string? newPhase = string.IsNullOrWhiteSpace(_phaseRenameNewKey) ? null : _phaseRenameNewKey.Trim();
            foreach (var m in profile.Mechanics)
            {
                var current = string.IsNullOrEmpty(m.Phase) ? "（未分類）" : m.Phase!;
                if (string.Equals(current, _phaseRenameOldKey, StringComparison.Ordinal))
                {
                    m.Phase = newPhase;
                }
            }
            _dirty = true;
            _phaseRenameOldKey = null;
            _phaseRenameProfileId = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("キャンセル", new Vector2(120f * ImGuiHelpers.GlobalScale, 0f)))
        {
            _phaseRenameOldKey = null;
            _phaseRenameProfileId = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void DrawPhaseDeletePopup(StrategyProfile profile)
    {
        if (_shouldOpenPhaseDelete && profile.Id == _phaseDeleteProfileId)
        {
            _shouldOpenPhaseDelete = false;
            ImGui.OpenPopup("##phase-delete-popup");
        }
        if (profile.Id != _phaseDeleteProfileId) return;
        ImGui.SetNextWindowSize(new Vector2(380f * ImGuiHelpers.GlobalScale, 0f));
        if (!ImGui.BeginPopupModal("##phase-delete-popup", ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.TextWrapped($"フェーズ「{_phaseDeleteKey}」のメカニクスを**全件削除**します。よろしいですか？");
        ImGui.Spacing();
        if (ImGui.Button("削除する", new Vector2(140f * ImGuiHelpers.GlobalScale, 0f)))
        {
            profile.Mechanics.RemoveAll(m =>
            {
                var current = string.IsNullOrEmpty(m.Phase) ? "（未分類）" : m.Phase!;
                return string.Equals(current, _phaseDeleteKey, StringComparison.Ordinal);
            });
            _dirty = true;
            _phaseDeleteKey = null;
            _phaseDeleteProfileId = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("キャンセル", new Vector2(120f * ImGuiHelpers.GlobalScale, 0f)))
        {
            _phaseDeleteKey = null;
            _phaseDeleteProfileId = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void DrawMechanicStrategyEditor(StrategyProfile profile, MechanicStrategy mechanic, int index)
    {
        var enabled = mechanic.Enabled;
        if (ImGui.Checkbox("有効##mechanic-enabled", ref enabled))
        {
            mechanic.Enabled = enabled;
            _dirty = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("OFF にするとこのメカニクスは戦闘中に発動しない");

        // ライブプレビュー：戦闘中の発動を擬似的にミニマップへ流し、レイアウトを目視確認できる
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.20f, 0.55f, 0.20f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.30f, 0.65f, 0.30f, 1f));
        if (ImGui.Button($"▶ プレビュー##mechanic-preview-{mechanic.Id}"))
        {
            FireMechanicPreview(profile, mechanic);
        }
        ImGui.PopStyleColor(2);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("このメカニクスをいま発動させたかのようにミニマップを描画。\n" +
                              "戦闘外でも、配置の見た目を確認するのに使える。");
        }

        ImGui.SameLine();
        // フェーズ：プルダウン（既存フェーズ + 未分類 + 新規...）+ 自由入力
        var existingPhases = profile.Mechanics
            .Select(m => m.Phase)
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var phaseChoices = new[] { "（未分類）" }.Concat(existingPhases).Concat(new[] { "新規..." }).ToArray();
        var phaseChoiceKeys = new[] { (string?)null }
            .Concat(existingPhases.Select(p => (string?)p))
            .Concat(new[] { (string?)"__NEW__" })
            .ToArray();
        var curIdx = 0;
        for (var k = 1; k < phaseChoiceKeys.Length; k++)
        {
            if (string.Equals(phaseChoiceKeys[k], mechanic.Phase, StringComparison.OrdinalIgnoreCase))
            {
                curIdx = k;
                break;
            }
        }
        ImGui.SetNextItemWidth(140f * ImGuiHelpers.GlobalScale);
        if (ImGui.Combo("フェーズ##mechanic-phase-combo", ref curIdx, phaseChoices, phaseChoices.Length))
        {
            var picked = phaseChoiceKeys[curIdx];
            if (picked == "__NEW__")
            {
                // 新規フェーズ名を popup で受け取って、このメカニクスをそこに移動
                _movePhaseProfileId = profile.Id;
                _movePhaseMechanicId = mechanic.Id;
                _movePhaseNewName = $"P{Math.Max(1, existingPhases.Length + 1)}";
                ImGui.OpenPopup("##move-phase-new-popup");
            }
            else
            {
                mechanic.Phase = picked;
                _dirty = true;
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("このメカニクスがどのフェーズに属するか。\n" +
                              "プルダウンから選ぶだけで他フェーズに移動。\n" +
                              "「新規...」を選ぶと新しいフェーズを作って即移動。");
        }
        ImGui.SameLine();
        var customPhase = mechanic.Phase ?? string.Empty;
        ImGui.SetNextItemWidth(140f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("自由入力##mechanic-phase-free", ref customPhase, 32))
        {
            mechanic.Phase = string.IsNullOrWhiteSpace(customPhase) ? null : customPhase;
            _dirty = true;
        }
        DrawMovePhaseNewPopup(profile);

        ImGui.SameLine();
        if (ImGui.SmallButton("メカニクスを削除"))
        {
            profile.Mechanics.RemoveAt(index);
            _dirty = true;
            return;
        }

        // ランダム分岐対応：「同じ瞬間に発動するけど形状が違う」もう片方の選択肢を作る用。
        // 例：ボスがドーナツ AoE / 円 AoE のどちらかをランダムで撃つ → 2 つメカニクスを作って
        //     それぞれ別 cast_id にトリガー紐付けすれば、出た方だけが発動・描画される。
        ImGui.SameLine();
        if (ImGui.SmallButton("複製（ランダム分岐用）"))
        {
            var copy = StrategyPlanResolver.CloneMechanic(mechanic);
            copy.Id = UniqueMechanicId(profile, mechanic.Id + "_alt");
            copy.Label = (mechanic.Label ?? mechanic.Id) + " (alt)";
            // トリガーは消す：別の cast_id 等に紐付け直すのが利用想定なので空から
            copy.Triggers.Clear();
            copy.AttachedTo = null;
            profile.Mechanics.Insert(index + 1, copy);
            _dirty = true;
            return;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "現在のメカニクスを散開ポジ／オブジェクト／AoE まるごと複製します。\n" +
                "ランダムギミック分岐用：例えばボスが\n" +
                "  パターンA（ドーナツ）or パターンB（円形）をランダムに撃つ\n" +
                "ような場合、まず片方を作ってこのボタンで複製 →\n" +
                "複製側の AoE 形状を変更 → 2 つのメカニクスにそれぞれ別の cast_id を紐付け。\n" +
                "実戦で出た方の cast_id だけが発動・描画されます。");
        }

        var id = mechanic.Id;
        ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("ID##mechanic-id", ref id, 64))
        {
            mechanic.Id = string.IsNullOrWhiteSpace(id) ? mechanic.Id : id.Trim();
            _dirty = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("内部識別子（英数字推奨）");

        var label = mechanic.Label;
        ImGui.SetNextItemWidth(320f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("ラベル##mechanic-label", ref label, 128))
        {
            mechanic.Label = label;
            _dirty = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("UI に表示される名前。日本語可（例：「無の肥大」）");

        var time = (float)(mechanic.Time ?? 0);
        ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputFloat("発動時刻(s)##mechanic-time", ref time, 0.5f, 1f, "%.1f"))
        {
            mechanic.Time = time <= 0 ? null : time;
            _dirty = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("戦闘開始からの相対秒。録画から自動入力されるが手動で上書き可。\n" +
                                                     "「観測キャストに紐付け」している場合は録画 aggregate の値を使うので無視される");

        ImGui.SameLine();
        var duration = (float)(mechanic.Duration ?? 0);
        ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputFloat("継続時間##mechanic-duration", ref duration, 0.5f, 1f, "%.1f"))
        {
            mechanic.Duration = duration <= 0 ? null : duration;
            _dirty = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("ミニマップやタイムラインに表示し続ける秒数（0=即終了）");

        ImGui.SameLine();
        var warn = (float)(mechanic.AdvanceWarningSec ?? 0);
        ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputFloat("先行通知秒数##mechanic-warn", ref warn, 0.5f, 1f, "%.1f"))
        {
            mechanic.AdvanceWarningSec = warn <= 0 ? null : warn;
            _dirty = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("ギミック発動の何秒前に音声 / 視覚通知を出すか。\n" +
                                                     "5 にすると「5 秒前にカウントダウン開始」");

        DrawMechanicAttachEditor(profile, mechanic);
        DrawMechanicEvidence(mechanic);
        DrawMechanicTriggersEditor(mechanic);
        DrawPartyStatusHighlightsEditor(mechanic);

        // ロール／ジョブは「自分が tank のときだけこの通知を出す」のような絞り込み用。
        // ラベルだけだと意味が伝わりにくいのでヒント文を直接表示しておく。
        ImGui.TextDisabled("自分のロール／ジョブが一致したときだけ通知（空欄＝全員対象）");
        var role = mechanic.Role ?? string.Empty;
        ImGui.SetNextItemWidth(160f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("自分のロールが##mechanic-role", ref role, 32))
        {
            mechanic.Role = string.IsNullOrWhiteSpace(role) ? null : role;
            _dirty = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(
            "自分のロールが一致するときだけこのメカニクスを発動する。空欄なら全員対象。\n" +
            "使える値：tank / mt / st / healer / h1 / h2 / dps / melee / ranged / caster\n" +
            "例：「タンク用の AP マークの読み上げを MT だけに出したい」→ 'mt'");

        ImGui.SameLine();
        var job = mechanic.Job ?? string.Empty;
        ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("自分のジョブが##mechanic-job", ref job, 16))
        {
            mechanic.Job = string.IsNullOrWhiteSpace(job) ? null : job;
            _dirty = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(
            "自分のジョブが一致するときだけ発動。空欄なら全ジョブ対象。\n" +
            "PLD / WAR / GNB / DRK / WHM / SCH / SGE / AST / ... 等のジョブ略称\n" +
            "例：「ホリスピのみに通知」→ 'WHM'");

        var callout = mechanic.Callout ?? string.Empty;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("読み上げ文##mechanic-callout", ref callout, 256))
        {
            mechanic.Callout = string.IsNullOrWhiteSpace(callout) ? null : callout;
            _dirty = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("TTS で読み上げ + 中央オーバーレイの文字。\n" +
                                                     "例：「中央安置」「散開」「シェイク + ランパート」");

        var warning = mechanic.WarningText ?? string.Empty;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("先行通知文##mechanic-warning", ref warning, 256))
        {
            mechanic.WarningText = string.IsNullOrWhiteSpace(warning) ? null : warning;
            _dirty = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("先行通知秒数前に読み上げる文字。空欄なら callout を流用");

        // 旧 gimmick プルダウン（outer_ring / inner_circle 等）は、ユーザー定義レイアウトの
        // 補助としてのみ使う。多くの場合は「（なし）」で十分（下の AoE ゾーン / オブジェクトで描く）。
        var gimmickWithNone = new[] { "(なし)" }.Concat(ArenaViewGimmicks).ToArray();
        var labelWithNone = new[] { "（なし）" }.Concat(Localization.LocalizeAll(ArenaViewGimmicks, Localization.Gimmick)).ToArray();
        var currentGm = mechanic.Gimmick;
        var gimmickIndex = 0;
        if (!string.IsNullOrEmpty(currentGm))
        {
            for (var k = 1; k < gimmickWithNone.Length; k++)
            {
                if (string.Equals(gimmickWithNone[k], currentGm, StringComparison.OrdinalIgnoreCase)) { gimmickIndex = k; break; }
            }
        }
        ImGui.SetNextItemWidth(280f * ImGuiHelpers.GlobalScale);
        if (ImGui.Combo("旧プリセット形##mechanic-gimmick", ref gimmickIndex, labelWithNone, labelWithNone.Length))
        {
            mechanic.Gimmick = gimmickIndex == 0 ? null : gimmickWithNone[gimmickIndex];
            _dirty = true;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("既定は『（なし）』。下のオブジェクト / AoE / 散開ポジで自由に描いた\n" +
                              "レイアウトだけが表示される。\n" +
                              "ここで「outer_ring」などを選ぶと、その自動形（中央安置の点線円など）が\n" +
                              "下のレイアウトの背景として追加で描かれる（おすすめしない）。");
        }

        ImGui.SameLine();
        var color = mechanic.Color ?? string.Empty;
        ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("色 (#RRGGBB)##mechanic-color", ref color, 16))
        {
            mechanic.Color = string.IsNullOrWhiteSpace(color) ? null : color;
            _dirty = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("タイムライン / ノート上での色付け");

        // ミニマップを出すかどうかの簡素なトグル
        ImGui.Spacing();
        var disableMinimap = mechanic.DisableMinimap;
        if (ImGui.Checkbox("このギミックではミニマップを出さない##no-minimap", ref disableMinimap))
        {
            mechanic.DisableMinimap = disableMinimap;
            _dirty = true;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("ON にすると TTS / オーバーレイ通知だけ出して、ミニマップは描画しない。\n" +
                              "「タンクスワップ」など視覚的に何も出さなくていいギミックに使う。");
        }

        if (!mechanic.DisableMinimap)
        {
            DrawMechanicPositionPicker(profile, mechanic);
        }
        else
        {
            ImGui.TextDisabled("（ミニマップ非表示中。配置エディタは省略）");
        }

        // 安置計算（safe_zone）は古い JSON ベースの上級機能。普段は折りたたんで隠す。
        // ユーザー定義レイアウトで足りるケースが大半なので既定 OFF。
        if (ImGui.CollapsingHeader("詳細：安置計算 (safe_zone) — 上級者向け##sz-collapse"))
        {
            ImGui.TextDisabled("外部 JSON で安置の算出方法を指定する古い機構です。\n" +
                                "通常は「AoE 形状」と「散開ポジ」だけで十分。空にしておけば動作に影響しません。");
            var safeZoneAction = new ActionDefinition { SafeZone = mechanic.SafeZone };
            DrawSafeZoneSubEditor(safeZoneAction);
            mechanic.SafeZone = safeZoneAction.SafeZone;
        }
    }

    /// <summary>
    /// このメカニクス専用の散開ポジ編集 UI。インラインで地図エディタを描画する。
    /// 各メカニクスごとに独立したドラッグ可能なミニマップ。
    /// </summary>
    private void DrawMechanicPositionPicker(StrategyProfile profile, MechanicStrategy mechanic)
    {
        DrawMechanicArenaShapeOverride(profile, mechanic);

        ImGui.Spacing();
        ImGui.TextUnformatted("散開ポジション（このギミック）");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "このギミックの瞬間に PT がどこに立つか。\n" +
                "ドット をドラッグで移動。空白部分をダブルクリックで追加。\n" +
                "ボタンで 8 方向ポジを一発生成も可。");
        }

        ImGui.SameLine();
        if (ImGui.SmallButton($"8 方向ポジ生成##mech-fill-{mechanic.Id}"))
        {
            // このメカニクス（フェーズ／プロファイル継承を含む）の寸法に合わせてスケール。
            var (halfW, halfD) = ResolveArenaHalfExtents(profile, mechanic);
            foreach (var pos in CreateEightWaySpreadPositions(halfW, halfD))
            {
                if (mechanic.SpreadPositions.Any(p => string.Equals(p.Slot, pos.Slot, StringComparison.OrdinalIgnoreCase)))
                    continue;
                mechanic.SpreadPositions.Add(pos);
            }
            _dirty = true;
        }
        ImGui.SameLine();
        if (mechanic.SpreadPositions.Count > 0 && ImGui.SmallButton($"全クリア##mech-clear-{mechanic.Id}"))
        {
            mechanic.SpreadPositions.Clear();
            _dirty = true;
        }

        // オブジェクトマーカー / AoE ゾーンを追加するボタン
        if (ImGui.SmallButton($"オブジェクト追加##mech-add-obj-{mechanic.Id}"))
        {
            mechanic.ObjectMarkers.Add(new StrategyObjectMarker
            {
                Id = $"obj_{mechanic.ObjectMarkers.Count + 1}",
                Label = $"obj{mechanic.ObjectMarkers.Count + 1}",
                X = 0, Z = 0,
                Color = "#F66B6B",
                Shape = "circle",
            });
            _dirty = true;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "ボス位置・add NPC 位置・誘導目標などの目印を地図に置く。\n" +
                "追加後、下の「オブジェクト一覧」で『ウェイマーク連動』列を選ぶと\n" +
                "FF14 のフィールドマーカー（A/B/C/D/1-4）に追従させられる。\n" +
                "（PT が当日マーカーを置くだけで自動的にこの位置が動く）");
        }
        ImGui.SameLine();
        if (ImGui.SmallButton($"AoE円##mech-add-circle-{mechanic.Id}"))
        {
            mechanic.AoeZones.Add(new StrategyAoeZone { Shape = "circle", X = 0, Z = 0, RadiusM = 5, IsDanger = true });
            _dirty = true;
        }
        ImGui.SameLine();
        if (ImGui.SmallButton($"AoEドーナツ##mech-add-donut-{mechanic.Id}"))
        {
            mechanic.AoeZones.Add(new StrategyAoeZone { Shape = "donut", X = 0, Z = 0, RadiusM = 10, InnerRadiusM = 4, IsDanger = true });
            _dirty = true;
        }
        ImGui.SameLine();
        if (ImGui.SmallButton($"AoE扇形##mech-add-cone-{mechanic.Id}"))
        {
            mechanic.AoeZones.Add(new StrategyAoeZone { Shape = "cone", X = 0, Z = 0, RadiusM = 10, RotationDeg = -90, FanDeg = 90, IsDanger = true });
            _dirty = true;
        }
        ImGui.SameLine();
        if (ImGui.SmallButton($"AoE矩形##mech-add-rect-{mechanic.Id}"))
        {
            mechanic.AoeZones.Add(new StrategyAoeZone { Shape = "rect", X = 0, Z = 0, RadiusM = 10, HalfWidthM = 2.5, RotationDeg = 0, IsDanger = true });
            _dirty = true;
        }
        ImGui.SameLine();
        if (ImGui.SmallButton($"AoE直線##mech-add-line-{mechanic.Id}"))
        {
            mechanic.AoeZones.Add(new StrategyAoeZone
            {
                Shape = "line",
                X = 0,
                Z = 0,
                RadiusM = 40,
                HalfWidthM = AoeGeometryPolicy.DefaultLineHalfWidthM,
                RotationDeg = 0,
                IsDanger = true,
            });
            _dirty = true;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("直線 AoE。基準を「一致obj全部」にすると、小ブラックホール2個から2本の直線のような分岐を作れます。");
        }
        ImGui.SameLine();
        if (ImGui.SmallButton($"安置(緑)##mech-add-safe-{mechanic.Id}"))
        {
            mechanic.AoeZones.Add(new StrategyAoeZone { Shape = "circle", X = 0, Z = 0, RadiusM = 3, IsDanger = false });
            _dirty = true;
        }

        // 各メカニクス用に擬似 profile を用意してドラッグ式エディタを再利用する。
        // SpreadPositions は同じ List 参照、ObjectMarkers / AoeZones は mechanic 経由で渡す。
        ImGui.PushID($"mech-map-{mechanic.Id}");
        var fakeProfile = new StrategyProfile
        {
            Id = $"{profile.Id}::{mechanic.Id}",
            ArenaShape = profile.ArenaShape,
            ArenaRadius = profile.ArenaRadius,
            ArenaWidth = profile.ArenaWidth,
            ArenaDepth = profile.ArenaDepth,
            SpreadPositions = mechanic.SpreadPositions,
        };
        DrawSpreadPositionMapEditor(fakeProfile, mechanic);
        ImGui.PopID();
        ImGui.TextDisabled("↑ PT ドットドラッグ / オブジェクトドラッグ / AoE 中心ハンドルでドラッグ。空白ダブルクリックで PT ポジ追加。");

        // プロパティ表（オブジェクトと AoE の詳細編集）
        DrawSpreadPositionTable(mechanic);
        DrawObjectMarkerTable(mechanic);
        DrawAoeZoneTable(mechanic);
    }

    /// <summary>
    /// PT 散開ポジ（mechanic.SpreadPositions）の編集表。
    /// スロット名（MT/ST/H1/H2/D1-D4 など）の rename と削除ができる。
    /// </summary>
    private void DrawSpreadPositionTable(MechanicStrategy mechanic)
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("PT 散開ポジ一覧");
        if (mechanic.SpreadPositions.Count == 0)
        {
            ImGui.TextDisabled("（地図でダブルクリックして追加。または「8 方向ポジ生成」ボタン）");
            return;
        }

        if (!ImGui.BeginTable($"##pt-table-{mechanic.Id}", 8,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }

        ImGui.TableSetupColumn("スロット", ImGuiTableColumnFlags.WidthFixed, 80f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("→既存に変更", ImGuiTableColumnFlags.WidthFixed, 110f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("表示名", ImGuiTableColumnFlags.WidthFixed, 80f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("ロール", ImGuiTableColumnFlags.WidthFixed, 90f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("ジョブ", ImGuiTableColumnFlags.WidthFixed, 60f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("色", ImGuiTableColumnFlags.WidthFixed, 90f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("座標", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 56f * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        // クイック切替プリセット
        var slotPresets = new[] { "—", "MT", "ST", "H1", "H2", "D1", "D2", "D3", "D4" };

        for (var i = 0; i < mechanic.SpreadPositions.Count; i++)
        {
            var sp = mechanic.SpreadPositions[i];
            ImGui.PushID($"pt-row-{mechanic.Id}-{i}");
            ImGui.TableNextRow();

            // スロット直接編集（自由入力）
            ImGui.TableNextColumn();
            var slot = sp.Slot ?? string.Empty;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##slot", ref slot, 32))
            {
                sp.Slot = slot;
                _dirty = true;
            }

            // ワンクリックで MT/ST/... に切り替えるコンボ
            ImGui.TableNextColumn();
            var presetIdx = 0;
            for (var k = 1; k < slotPresets.Length; k++)
            {
                if (string.Equals(slotPresets[k], sp.Slot, StringComparison.OrdinalIgnoreCase))
                {
                    presetIdx = k;
                    break;
                }
            }
            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo("##quick", ref presetIdx, slotPresets, slotPresets.Length))
            {
                if (presetIdx != 0)
                {
                    var preset = slotPresets[presetIdx];
                    var oldSlot = sp.Slot;
                    sp.Slot = preset;
                    // ラベル / 色も追従
                    if (string.IsNullOrEmpty(sp.Label) ||
                        string.Equals(sp.Label, oldSlot, StringComparison.OrdinalIgnoreCase))
                    {
                        sp.Label = preset;
                    }
                    sp.Color = preset switch
                    {
                        "MT" or "ST" => "#60A5FA",
                        "H1" or "H2" => "#34D399",
                        "D1" or "D2" => "#F87171",
                        _ => "#FBBF24",
                    };
                    _dirty = true;
                }
            }

            ImGui.TableNextColumn();
            var label = sp.Label ?? string.Empty;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##label", ref label, 32))
            {
                sp.Label = string.IsNullOrWhiteSpace(label) ? null : label;
                _dirty = true;
            }

            ImGui.TableNextColumn();
            var role = sp.Role ?? string.Empty;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##role", ref role, 32))
            {
                sp.Role = string.IsNullOrWhiteSpace(role) ? null : role;
                _dirty = true;
            }

            ImGui.TableNextColumn();
            var job = sp.Job ?? string.Empty;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##job", ref job, 16))
            {
                sp.Job = string.IsNullOrWhiteSpace(job) ? null : job;
                _dirty = true;
            }

            ImGui.TableNextColumn();
            var color = sp.Color ?? string.Empty;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##color", ref color, 16))
            {
                sp.Color = string.IsNullOrWhiteSpace(color) ? null : color;
                _dirty = true;
            }

            ImGui.TableNextColumn();
            // 手入力で座標を直接編集できるようにする（精密配置用。地図ドラッグの代替）。
            var spX = (float)sp.X;
            var spZ = (float)sp.Z;
            ImGui.SetNextItemWidth(70f * ImGuiHelpers.GlobalScale);
            if (ImGui.InputFloat("X##sp-x", ref spX, 0.5f, 1f, "%.1f"))
            {
                sp.X = spX;
                _dirty = true;
            }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(70f * ImGuiHelpers.GlobalScale);
            if (ImGui.InputFloat("Z##sp-z", ref spZ, 0.5f, 1f, "%.1f"))
            {
                sp.Z = spZ;
                _dirty = true;
            }

            ImGui.TableNextColumn();
            if (ImGui.SmallButton("削除"))
            {
                mechanic.SpreadPositions.RemoveAt(i);
                _dirty = true;
                ImGui.PopID();
                break;
            }

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void DrawObjectMarkerTable(MechanicStrategy mechanic)
    {
        if (mechanic.ObjectMarkers.Count == 0) return;
        ImGui.Spacing();
        ImGui.TextUnformatted("オブジェクト一覧");
        if (ImGui.BeginTable($"##obj-table-{mechanic.Id}", 7,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthFixed, 70f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("ラベル", ImGuiTableColumnFlags.WidthFixed, 90f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("形", ImGuiTableColumnFlags.WidthFixed, 90f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("色", ImGuiTableColumnFlags.WidthFixed, 90f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("ウェイマーク連動", ImGuiTableColumnFlags.WidthFixed, 130f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("座標", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 56f * ImGuiHelpers.GlobalScale);
            ImGui.TableHeadersRow();

            var waymarkOptions = new[] { "(なし)", "A", "B", "C", "D", "1", "2", "3", "4" };
            for (var i = 0; i < mechanic.ObjectMarkers.Count; i++)
            {
                var mk = mechanic.ObjectMarkers[i];
                ImGui.PushID($"obj-row-{mechanic.Id}-{i}");
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var id = mk.Id;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("##id", ref id, 32)) { mk.Id = id; _dirty = true; }
                ImGui.TableNextColumn();
                var lbl = mk.Label ?? string.Empty;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("##label", ref lbl, 32))
                {
                    mk.Label = string.IsNullOrWhiteSpace(lbl) ? null : lbl; _dirty = true;
                }
                ImGui.TableNextColumn();
                var shapeIdx = (mk.Shape ?? "circle") switch { "square" => 1, "triangle" => 2, "diamond" => 3, _ => 0 };
                ImGui.SetNextItemWidth(-1);
                if (ImGui.Combo("##shape", ref shapeIdx, new[] { "○ 円", "□ 四角", "△ 三角", "◇ 菱形" }, 4))
                {
                    mk.Shape = shapeIdx switch { 1 => "square", 2 => "triangle", 3 => "diamond", _ => "circle" };
                    _dirty = true;
                }
                ImGui.TableNextColumn();
                var color = mk.Color ?? string.Empty;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("##color", ref color, 16))
                {
                    mk.Color = string.IsNullOrWhiteSpace(color) ? null : color; _dirty = true;
                }
                ImGui.TableNextColumn();
                var wmIdx = 0;
                for (var k = 1; k < waymarkOptions.Length; k++)
                {
                    if (string.Equals(waymarkOptions[k], mk.Waymark, StringComparison.OrdinalIgnoreCase)) { wmIdx = k; break; }
                }
                ImGui.SetNextItemWidth(-1);
                if (ImGui.Combo("##waymark", ref wmIdx, waymarkOptions, waymarkOptions.Length))
                {
                    mk.Waymark = wmIdx == 0 ? null : waymarkOptions[wmIdx];
                    _dirty = true;
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("FFXIV のフィールドマーカーに紐付ける。\n" +
                                      "戦闘中はそのマーカーの実位置にこのオブジェクトが追従する。");
                }
                ImGui.TableNextColumn();
                if (string.IsNullOrEmpty(mk.Waymark))
                {
                    var ox = (float)mk.X;
                    var oz = (float)mk.Z;
                    ImGui.SetNextItemWidth(70f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputFloat("X##obj-x", ref ox, 0.5f, 1f, "%.1f"))
                    {
                        mk.X = ox; _dirty = true;
                    }
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(70f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputFloat("Z##obj-z", ref oz, 0.5f, 1f, "%.1f"))
                    {
                        mk.Z = oz; _dirty = true;
                    }
                }
                else
                {
                    ImGui.TextDisabled($"※マーカー {mk.Waymark} 追従");
                }
                ImGui.TableNextColumn();
                if (ImGui.SmallButton("削除")) { mechanic.ObjectMarkers.RemoveAt(i); _dirty = true; ImGui.PopID(); break; }
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
    }

    private void DrawAoeZoneTable(MechanicStrategy mechanic)
    {
        if (mechanic.AoeZones.Count == 0) return;
        ImGui.Spacing();
        ImGui.TextUnformatted("AoE 形状一覧");
        if (ImGui.BeginTable($"##aoe-table-{mechanic.Id}", 9,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("形", ImGuiTableColumnFlags.WidthFixed, 92f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("半径(m)", ImGuiTableColumnFlags.WidthFixed, 70f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("内径(m)", ImGuiTableColumnFlags.WidthFixed, 70f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("方向(°)", ImGuiTableColumnFlags.WidthFixed, 70f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("扇角/半幅", ImGuiTableColumnFlags.WidthFixed, 80f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("危険/安置", ImGuiTableColumnFlags.WidthFixed, 92f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("基準", ImGuiTableColumnFlags.WidthFixed, 120f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("ラベル / 座標", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 56f * ImGuiHelpers.GlobalScale);
            ImGui.TableHeadersRow();

            for (var i = 0; i < mechanic.AoeZones.Count; i++)
            {
                var z = mechanic.AoeZones[i];
                ImGui.PushID($"aoe-row-{mechanic.Id}-{i}");
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                var shapeIdx = (z.Shape ?? "circle") switch { "donut" => 1, "cone" => 2, "rect" => 3, "line" => 4, _ => 0 };
                ImGui.SetNextItemWidth(-1);
                if (ImGui.Combo("##shape", ref shapeIdx, new[] { "円", "ドーナツ", "扇形", "矩形", "直線" }, 5))
                {
                    z.Shape = shapeIdx switch { 1 => "donut", 2 => "cone", 3 => "rect", 4 => "line", _ => "circle" };
                    _dirty = true;
                }

                ImGui.TableNextColumn();
                var rad = (float)z.RadiusM;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputFloat("##rad", ref rad, 0.5f, 1f, "%.1f"))
                {
                    z.RadiusM = Math.Max(0.5, rad); _dirty = true;
                }

                ImGui.TableNextColumn();
                if (z.Shape == "donut")
                {
                    var inner = (float)(z.InnerRadiusM ?? z.RadiusM * 0.5);
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.InputFloat("##inner", ref inner, 0.5f, 1f, "%.1f"))
                    {
                        z.InnerRadiusM = Math.Max(0, inner); _dirty = true;
                    }
                }
                else
                {
                    ImGui.TextDisabled("—");
                }

                ImGui.TableNextColumn();
                if (z.Shape is "cone" or "rect" or "line")
                {
                    var rot = (float)(z.RotationDeg ?? 0);
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.InputFloat("##rot", ref rot, 5f, 15f, "%.0f"))
                    {
                        z.RotationDeg = rot; _dirty = true;
                    }
                }
                else { ImGui.TextDisabled("—"); }

                ImGui.TableNextColumn();
                if (z.Shape == "cone")
                {
                    var fan = (float)(z.FanDeg ?? 90);
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.InputFloat("##fan", ref fan, 5f, 15f, "%.0f"))
                    {
                        z.FanDeg = Math.Max(5, fan); _dirty = true;
                    }
                }
                else if (z.Shape is "rect" or "line")
                {
                    var hw = (float)(z.HalfWidthM ?? AoeGeometryPolicy.DefaultLineHalfWidthM);
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.InputFloat("##hw", ref hw, 0.5f, 1f, "%.1f"))
                    {
                        z.HalfWidthM = Math.Max(0.5, hw); _dirty = true;
                    }
                }
                else { ImGui.TextDisabled("—"); }

                ImGui.TableNextColumn();
                var dangerIdx = z.IsDanger ? 0 : 1;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.Combo("##danger", ref dangerIdx, new[] { "危険(赤)", "安置(緑)" }, 2))
                {
                    z.IsDanger = (dangerIdx == 0); _dirty = true;
                }

                ImGui.TableNextColumn();
                var anchorOptions = new[] { "固定", "発火元", "一致obj", "一致obj全部", "ウェイマーク" };
                var anchorIdx = (z.Anchor ?? "static") switch
                {
                    "source_actor" => 1,
                    "matched_object" => 2,
                    "each_matched_object" => 3,
                    "waymark" => 4,
                    _ => 0,
                };
                ImGui.SetNextItemWidth(-1);
                if (ImGui.Combo("##anchor", ref anchorIdx, anchorOptions, anchorOptions.Length))
                {
                    z.Anchor = anchorIdx switch
                    {
                        1 => "source_actor",
                        2 => "matched_object",
                        3 => "each_matched_object",
                        4 => "waymark",
                        _ => "static",
                    };
                    _dirty = true;
                }
                if (anchorIdx == 4)
                {
                    var waymarks = new[] { "A", "B", "C", "D", "1", "2", "3", "4" };
                    var wi = Array.FindIndex(waymarks, w => string.Equals(w, z.AnchorWaymark, StringComparison.OrdinalIgnoreCase));
                    if (wi < 0) wi = 0;
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.Combo("##anchor-waymark", ref wi, waymarks, waymarks.Length))
                    {
                        z.AnchorWaymark = waymarks[wi];
                        _dirty = true;
                    }
                }

                ImGui.TableNextColumn();
                var lbl = z.Label ?? string.Empty;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("##label", ref lbl, 32))
                {
                    z.Label = string.IsNullOrWhiteSpace(lbl) ? null : lbl; _dirty = true;
                }
                // 中心座標も手入力可能（地図ドラッグの代替。精密配置用）
                var zx = (float)z.X;
                var zz = (float)z.Z;
                ImGui.SetNextItemWidth(70f * ImGuiHelpers.GlobalScale);
                if (ImGui.InputFloat("X##aoe-x", ref zx, 0.5f, 1f, "%.1f"))
                {
                    z.X = zx; _dirty = true;
                }
                ImGui.SameLine();
                ImGui.SetNextItemWidth(70f * ImGuiHelpers.GlobalScale);
                if (ImGui.InputFloat("Z##aoe-z", ref zz, 0.5f, 1f, "%.1f"))
                {
                    z.Z = zz; _dirty = true;
                }

                ImGui.TableNextColumn();
                if (ImGui.SmallButton("削除")) { mechanic.AoeZones.RemoveAt(i); _dirty = true; ImGui.PopID(); break; }
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
    }

    /// <summary>
    /// メカニクスの「発動条件リスト」(MechanicTriggers) を編集する UI。
    /// cast / action_used / status_gain / status_lose / rotation のいずれか。
    /// 複数列挙すると OR 結合（どれか 1 つで発動）。
    /// </summary>
    private void DrawMechanicTriggersEditor(MechanicStrategy mechanic)
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("発動条件（OR 結合 / 空ならキャスト紐付けにフォールバック）");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "「このメカニクスが発動する瞬間」を複数の条件で書ける。\n" +
                "・cast: 敵が指定キャストを開始した\n" +
                "・action_used: 即時アクション（無詠唱）\n" +
                "・status_gain: 自分や PT に指定バフ／デバフが付いた\n" +
                "・status_lose: 同 消えた\n" +
                "・rotation: 指定アクターが指定方向を向いた");
        }

        if (ImGui.SmallButton($"+ キャスト##trig-add-cast-{mechanic.Id}"))
        {
            mechanic.Triggers.Add(new MechanicTrigger { Type = "cast", Match = new MatchCondition() });
            _dirty = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("敵がキャストを開始した瞬間");
        ImGui.SameLine();
        if (ImGui.SmallButton($"+ ステータス付与##trig-add-sg-{mechanic.Id}"))
        {
            mechanic.Triggers.Add(new MechanicTrigger { Type = "status_gain", Match = new MatchCondition() });
            _dirty = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("特定のバフ／デバフが付いた瞬間");
        ImGui.SameLine();
        if (ImGui.SmallButton($"+ ステータス消失##trig-add-sl-{mechanic.Id}"))
        {
            mechanic.Triggers.Add(new MechanicTrigger { Type = "status_lose", Match = new MatchCondition() });
            _dirty = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("特定のバフ／デバフが消えた瞬間");
        ImGui.SameLine();
        if (ImGui.SmallButton($"+ アクション使用##trig-add-au-{mechanic.Id}"))
        {
            mechanic.Triggers.Add(new MechanicTrigger { Type = "action_used", Match = new MatchCondition() });
            _dirty = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("詠唱なしの即時アクションが使われた瞬間");
        ImGui.SameLine();
        if (ImGui.SmallButton($"+ 向き##trig-add-rot-{mechanic.Id}"))
        {
            mechanic.Triggers.Add(new MechanicTrigger { Type = "rotation", FacingDeg = 0, FacingToleranceDeg = 30 });
            _dirty = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("特定アクターが特定方向を向いたとき");
        ImGui.SameLine();
        if (ImGui.SmallButton($"+ HP%##trig-add-hp-{mechanic.Id}"))
        {
            mechanic.Triggers.Add(new MechanicTrigger { Type = "hp_pct", HpPctBelow = 80 });
            _dirty = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("ボス HP がしきい値を跨いだとき（フェーズ移行）");
        ImGui.SameLine();
        if (ImGui.SmallButton($"+ オブジェクト##trig-add-obj-{mechanic.Id}"))
        {
            mechanic.Triggers.Add(new MechanicTrigger { Type = "object_appear", DedupSec = 1.0 });
            _dirty = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("ブラックホールや塔など、特定オブジェクトが出現した瞬間");
        ImGui.SameLine();
        if (ImGui.SmallButton($"+ オブジェクト集合##trig-add-obj-group-{mechanic.Id}"))
        {
            mechanic.Triggers.Add(new MechanicTrigger { Type = "object_group", ObjectCountMin = 2, ObjectWindowSec = 1.5, DedupSec = 5.0 });
            _dirty = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("同じオブジェクトが短時間に N 個出たら発動。ランダム分岐や複数体ギミック向け。");

        for (var i = 0; i < mechanic.Triggers.Count; i++)
        {
            var trig = mechanic.Triggers[i];
            ImGui.PushID($"trig-row-{mechanic.Id}-{i}");
            ImGui.Indent();
            var typeLabel = trig.Type switch
            {
                "cast" => "キャスト",
                "status_gain" => "ステータス付与",
                "status_lose" => "ステータス消失",
                "action_used" => "アクション使用",
                "rotation" => "向き",
                "hp_pct" => "HP%",
                "object_appear" => "オブジェクト出現",
                "object_group" => "オブジェクト集合",
                _ => trig.Type,
            };
            ImGui.TextUnformatted($"[{i + 1}] {typeLabel}");
            ImGui.SameLine();
            if (ImGui.SmallButton("削除"))
            {
                mechanic.Triggers.RemoveAt(i);
                _dirty = true;
                ImGui.Unindent();
                ImGui.PopID();
                break;
            }

            switch (trig.Type)
            {
                case "cast":
                case "action_used":
                {
                    trig.Match ??= new MatchCondition();
                    var trigRef = trig; // closure 用キャプチャ
                    if (ImGui.SmallButton($"📋 録画から選ぶ##pick-cast-{i}"))
                    {
                        var et = trigRef.Type == "cast" ? "cast_start" : "action_used";
                        OpenIdPicker(et, (id, name) =>
                        {
                            trigRef.Match ??= new MatchCondition();
                            if (trigRef.Type == "cast")
                            {
                                trigRef.Match.CastId = id;
                                trigRef.Match.CastName = name;
                            }
                            else
                            {
                                trigRef.Match.ActionId = id;
                                trigRef.Match.ActionName = name;
                            }
                            _dirty = true;
                        });
                    }
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("このゾーンの録画から観測済みキャスト/アクションを「名前: ID」リストで選ぶ。\n" +
                                          "ID を手で書き写す必要なし。");
                    }
                    ImGui.SameLine();
                    var castId = trig.Match.CastId ?? trig.Match.ActionId ?? "";
                    ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputText($"ID##cast-{i}", ref castId, 32))
                    {
                        if (trig.Type == "cast") trig.Match.CastId = string.IsNullOrWhiteSpace(castId) ? null : castId;
                        else trig.Match.ActionId = string.IsNullOrWhiteSpace(castId) ? null : castId;
                        _dirty = true;
                    }
                    ImGui.SameLine();
                    var castName = trig.Match.CastName ?? trig.Match.ActionName ?? "";
                    ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputText($"名前##cast-name-{i}", ref castName, 64))
                    {
                        if (trig.Type == "cast") trig.Match.CastName = string.IsNullOrWhiteSpace(castName) ? null : castName;
                        else trig.Match.ActionName = string.IsNullOrWhiteSpace(castName) ? null : castName;
                        _dirty = true;
                    }
                    break;
                }
                case "status_gain":
                case "status_lose":
                {
                    trig.Match ??= new MatchCondition();
                    var trigRef = trig;
                    if (ImGui.SmallButton($"📋 録画から選ぶ##pick-status-{i}"))
                    {
                        OpenIdPicker("status_gain", (id, name) =>
                        {
                            trigRef.Match ??= new MatchCondition();
                            if (uint.TryParse(id, out var sid)) trigRef.Match.StatusId = sid;
                            trigRef.Match.StatusName = name;
                            _dirty = true;
                        });
                    }
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("録画から観測済みステータス（バフ／デバフ）を「名前: ID」で選ぶ。");
                    }
                    ImGui.SameLine();
                    var sidStr = trig.Match.StatusId?.ToString() ?? "";
                    ImGui.SetNextItemWidth(110f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputText($"ID##sid-{i}", ref sidStr, 16))
                    {
                        if (uint.TryParse(sidStr, out var sid)) trig.Match.StatusId = sid;
                        else trig.Match.StatusId = null;
                        _dirty = true;
                    }
                    ImGui.SameLine();
                    var sname = trig.Match.StatusName ?? "";
                    ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputText($"名前##sname-{i}", ref sname, 64))
                    {
                        trig.Match.StatusName = string.IsNullOrWhiteSpace(sname) ? null : sname;
                        _dirty = true;
                    }
                    break;
                }
                case "rotation":
                {
                    var aname = trig.ActorName ?? "";
                    ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputText($"アクター名（部分一致）##rot-name-{i}", ref aname, 64))
                    {
                        trig.ActorName = string.IsNullOrWhiteSpace(aname) ? null : aname;
                        _dirty = true;
                    }

                    var face = (float)(trig.FacingDeg ?? 0);
                    ImGui.SetNextItemWidth(110f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputFloat($"向き(°) S=0/E=90/N=180/W=-90##rot-deg-{i}", ref face, 5f, 15f, "%.0f"))
                    {
                        trig.FacingDeg = face; _dirty = true;
                    }

                    var tol = (float)(trig.FacingToleranceDeg ?? 30);
                    ImGui.SetNextItemWidth(90f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputFloat($"許容±°##rot-tol-{i}", ref tol, 5f, 15f, "%.0f"))
                    {
                        trig.FacingToleranceDeg = Math.Max(1, tol); _dirty = true;
                    }
                    break;
                }
                case "hp_pct":
                {
                    var aname = trig.ActorName ?? "";
                    ImGui.SetNextItemWidth(200f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputText($"アクター名（部分一致）##hp-name-{i}", ref aname, 64))
                    {
                        trig.ActorName = string.IsNullOrWhiteSpace(aname) ? null : aname;
                        _dirty = true;
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("空欄なら全アクター対象。\nボス名を入れると特定ボスの HP% だけ監視。");

                    var hasBelow = trig.HpPctBelow.HasValue;
                    if (ImGui.Checkbox($"HP <##hp-below-on-{i}", ref hasBelow))
                    {
                        trig.HpPctBelow = hasBelow ? (trig.HpPctBelow ?? 80) : null;
                        _dirty = true;
                    }
                    if (hasBelow)
                    {
                        ImGui.SameLine();
                        var below = (float)(trig.HpPctBelow ?? 80);
                        ImGui.SetNextItemWidth(90f * ImGuiHelpers.GlobalScale);
                        if (ImGui.InputFloat($"%##hp-below-{i}", ref below, 1f, 5f, "%.0f"))
                        {
                            trig.HpPctBelow = Math.Clamp(below, 0, 100); _dirty = true;
                        }
                    }

                    ImGui.SameLine();
                    var hasAbove = trig.HpPctAbove.HasValue;
                    if (ImGui.Checkbox($"HP >##hp-above-on-{i}", ref hasAbove))
                    {
                        trig.HpPctAbove = hasAbove ? (trig.HpPctAbove ?? 50) : null;
                        _dirty = true;
                    }
                    if (hasAbove)
                    {
                        ImGui.SameLine();
                        var above = (float)(trig.HpPctAbove ?? 50);
                        ImGui.SetNextItemWidth(90f * ImGuiHelpers.GlobalScale);
                        if (ImGui.InputFloat($"%##hp-above-{i}", ref above, 1f, 5f, "%.0f"))
                        {
                            trig.HpPctAbove = Math.Clamp(above, 0, 100); _dirty = true;
                        }
                    }
                    break;
                }
                case "object_appear":
                case "object_group":
                {
                    var trigRef = trig;
                    if (ImGui.SmallButton($"📋 録画から選ぶ##pick-object-{i}"))
                    {
                        OpenIdPicker("object_appear", (id, name) =>
                        {
                            trigRef.ActorName = name;
                            if (uint.TryParse(id, out var dataId)) trigRef.ActorDataId = dataId;
                            _dirty = true;
                        });
                    }
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("録画から観測済みオブジェクトを選ぶ。名前だけでも動くので DataId を暗記しなくてよいです。");
                    }
                    ImGui.SameLine();
                    var objectName = trig.ActorName ?? trig.Match?.Actor ?? "";
                    ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputText($"オブジェクト名##obj-name-{i}", ref objectName, 64))
                    {
                        trig.ActorName = string.IsNullOrWhiteSpace(objectName) ? null : objectName;
                        _dirty = true;
                    }
                    ImGui.SameLine();
                    var dataId = trig.ActorDataId?.ToString() ?? "";
                    ImGui.SetNextItemWidth(110f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputText($"DataId##obj-data-{i}", ref dataId, 16))
                    {
                        if (uint.TryParse(dataId, out var did)) trig.ActorDataId = did;
                        else trig.ActorDataId = null;
                        _dirty = true;
                    }

                    if (trig.Type == "object_group")
                    {
                        var min = trig.ObjectCountMin ?? 2;
                        ImGui.SetNextItemWidth(90f * ImGuiHelpers.GlobalScale);
                        if (ImGui.InputInt($"最小個数##obj-min-{i}", ref min))
                        {
                            trig.ObjectCountMin = Math.Max(1, min); _dirty = true;
                        }
                        ImGui.SameLine();
                        var maxEnabled = trig.ObjectCountMax.HasValue;
                        if (ImGui.Checkbox($"最大指定##obj-max-on-{i}", ref maxEnabled))
                        {
                            trig.ObjectCountMax = maxEnabled ? Math.Max(trig.ObjectCountMin ?? 1, trig.ObjectCountMax ?? trig.ObjectCountMin ?? 1) : null;
                            _dirty = true;
                        }
                        if (maxEnabled)
                        {
                            ImGui.SameLine();
                            var max = trig.ObjectCountMax ?? min;
                            ImGui.SetNextItemWidth(90f * ImGuiHelpers.GlobalScale);
                            if (ImGui.InputInt($"最大個数##obj-max-{i}", ref max))
                            {
                                trig.ObjectCountMax = Math.Max(min, max); _dirty = true;
                            }
                        }
                        ImGui.SameLine();
                        var window = (float)(trig.ObjectWindowSec ?? 1.5);
                        ImGui.SetNextItemWidth(90f * ImGuiHelpers.GlobalScale);
                        if (ImGui.InputFloat($"秒窓##obj-win-{i}", ref window, 0.1f, 0.5f, "%.1f"))
                        {
                            trig.ObjectWindowSec = Math.Max(0.1, window); _dirty = true;
                        }
                    }
                    break;
                }
            }

            var dedup = (float)(trig.DedupSec ?? 1);
            ImGui.SetNextItemWidth(90f * ImGuiHelpers.GlobalScale);
            if (ImGui.InputFloat($"再発火抑制(秒)##dedup-{i}", ref dedup, 0.5f, 1f, "%.1f"))
            {
                trig.DedupSec = Math.Max(0, dedup); _dirty = true;
            }

            ImGui.Unindent();
            ImGui.PopID();
        }
    }

    /// <summary>
    /// PT メンバーが特定のバフ／デバフを持っている時に「色／バッジで強調」する設定。
    /// </summary>
    private void DrawPartyStatusHighlightsEditor(MechanicStrategy mechanic)
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("PT ステータス強調（持っている人だけ色／バッジ変更）");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "発動時にミニマップへ表示する PT ドットに対して、\n" +
                "「指定バフ／デバフを持っている人だけ色を変える / 横にバッジを出す」設定。\n" +
                "例：Tower 対象には黄色ドット + 🛡 バッジ、散開対象は白 + 💥。");
        }
        if (ImGui.SmallButton($"+ ハイライト追加##psh-add-{mechanic.Id}"))
        {
            mechanic.PartyStatusHighlights.Add(new StatusHighlightSpec
            {
                Color = "#FBBF24",
                Badge = "★",
            });
            _dirty = true;
        }

        for (var i = 0; i < mechanic.PartyStatusHighlights.Count; i++)
        {
            var hl = mechanic.PartyStatusHighlights[i];
            ImGui.PushID($"psh-row-{mechanic.Id}-{i}");
            ImGui.Indent();

            var hlRef = hl;
            if (ImGui.SmallButton($"📋 録画から選ぶ##psh-pick-{i}"))
            {
                OpenIdPicker("status_gain", (id, name) =>
                {
                    if (uint.TryParse(id, out var sid)) hlRef.StatusId = sid;
                    hlRef.StatusName = name;
                    _dirty = true;
                });
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("録画から観測済みステータスを選ぶ。\n" +
                                  "ID を覚えなくても、戦闘で観測されたバフ／デバフ名から選択できる。");
            }
            ImGui.SameLine();

            var sidStr = hl.StatusId?.ToString() ?? "";
            ImGui.SetNextItemWidth(100f * ImGuiHelpers.GlobalScale);
            if (ImGui.InputText("ID##psh-sid", ref sidStr, 16))
            {
                if (uint.TryParse(sidStr, out var sid)) hl.StatusId = sid;
                else hl.StatusId = null;
                _dirty = true;
            }

            ImGui.SameLine();
            var sname = hl.StatusName ?? "";
            ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
            if (ImGui.InputText("名前##psh-sname", ref sname, 64))
            {
                hl.StatusName = string.IsNullOrWhiteSpace(sname) ? null : sname;
                _dirty = true;
            }

            ImGui.SameLine();
            var color = hl.Color ?? "";
            ImGui.SetNextItemWidth(100f * ImGuiHelpers.GlobalScale);
            if (ImGui.InputText("色##psh-color", ref color, 16))
            {
                hl.Color = string.IsNullOrWhiteSpace(color) ? null : color;
                _dirty = true;
            }

            ImGui.SameLine();
            var badge = hl.Badge ?? "";
            ImGui.SetNextItemWidth(80f * ImGuiHelpers.GlobalScale);
            if (ImGui.InputText("バッジ##psh-badge", ref badge, 8))
            {
                hl.Badge = string.IsNullOrWhiteSpace(badge) ? null : badge;
                _dirty = true;
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("削除"))
            {
                mechanic.PartyStatusHighlights.RemoveAt(i);
                _dirty = true;
                ImGui.Unindent();
                ImGui.PopID();
                break;
            }

            ImGui.Unindent();
            ImGui.PopID();
        }
    }

    private void DrawMechanicAttachEditor(StrategyProfile profile, MechanicStrategy mechanic)
    {
        var attached = mechanic.AttachedTo?.CastName ?? mechanic.AttachedTo?.CastId ?? "（時刻指定）";
        ImGui.TextDisabled($"紐付け先: {attached}");
        ImGui.SameLine();
        if (ImGui.SmallButton("録画キャストに紐付け"))
        {
            _attachStrategyProfileId = profile.Id;
            _attachStrategyMechanicId = mechanic.Id;
            ImGui.OpenPopup("strategy-attach-popup");
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("録画から拾った特定 cast にこのメカニクスを紐付ける。\n" +
                                                     "紐付けると発動時刻が録画 aggregate から自動解決される。");
        ImGui.SameLine();
        if (mechanic.AttachedTo is not null && ImGui.SmallButton("紐付け解除"))
        {
            mechanic.AttachedTo = null;
            _dirty = true;
        }
    }

    private static void DrawMechanicEvidence(MechanicStrategy mechanic)
    {
        if (mechanic.ObservedCount is null &&
            mechanic.Confidence is null &&
            mechanic.TimeJitterSeconds is null)
        {
            return;
        }

        var confidence = mechanic.Confidence is { } c ? $"{c:P0}" : "-";
        var seen = mechanic.OccurrenceSeenCount is { } occurrenceSeen
            ? $"{occurrenceSeen}/{mechanic.ObservedCount ?? occurrenceSeen}"
            : $"{mechanic.ObservedCount ?? 0}";
        var jitter = mechanic.TimeJitterSeconds is { } j ? $"{j:0.0}s" : "-";
        ImGui.TextDisabled($"録画学習: {mechanic.SourceEventType ?? "event"} / 観測 {seen} / 信頼度 {confidence} / 時刻ばらつき {jitter}");
    }

    private void DrawStrategyAttachPopup()
    {
        ImGui.SetNextWindowSize(new Vector2(560f * ImGuiHelpers.GlobalScale, 420f * ImGuiHelpers.GlobalScale));
        if (!ImGui.BeginPopup("strategy-attach-popup"))
        {
            return;
        }

        if (_workingCopy is null ||
            string.IsNullOrEmpty(_attachStrategyProfileId) ||
            string.IsNullOrEmpty(_attachStrategyMechanicId))
        {
            ImGui.EndPopup();
            return;
        }

        var profile = _workingCopy.StrategyProfiles.FirstOrDefault(p =>
            string.Equals(p.Id, _attachStrategyProfileId, StringComparison.OrdinalIgnoreCase));
        var mechanic = profile?.Mechanics.FirstOrDefault(m =>
            string.Equals(m.Id, _attachStrategyMechanicId, StringComparison.OrdinalIgnoreCase));
        if (profile is null || mechanic is null)
        {
            ImGui.EndPopup();
            return;
        }

        ImGui.TextWrapped("Choose an observed cast from recordings. The mechanic will follow that cast timing on future pulls.");
        ImGui.Spacing();

        var agg = _recordingScanner.Aggregate(_workingZone);
        var filtered = ApplyEventFilters(agg);
        if (ImGui.BeginChild("##strategy-attach-list", new Vector2(-1, 320f * ImGuiHelpers.GlobalScale), true))
        {
            foreach (var ev in filtered)
            {
                if (ev.Key.Type != "cast_start")
                {
                    continue;
                }

                var label = $"[{ev.FirstSeenSeconds:0.0}s]  {ev.Key.Name ?? "?"}  ({ev.Key.Id ?? "-"})  x{ev.Count}";
                if (ImGui.Selectable(label))
                {
                    mechanic.AttachedTo = new MatchCondition
                    {
                        CastId = ev.Key.Id,
                        CastName = ev.Key.Name,
                    };
                    mechanic.Time = ev.FirstSeenSeconds;
                    _dirty = true;
                    ImGui.CloseCurrentPopup();
                }
            }
        }
        ImGui.EndChild();

        if (ImGui.Button("Cancel##strategy-attach-cancel"))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private string UniqueStrategyId(string prefix)
    {
        if (_workingCopy is null)
        {
            return prefix;
        }

        var index = _workingCopy.StrategyProfiles.Count + 1;
        string id;
        do
        {
            id = $"{prefix}_{index++}";
        }
        while (_workingCopy.StrategyProfiles.Any(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)));
        return id;
    }

    private static string UniqueMechanicId(StrategyProfile profile, string prefix)
    {
        var index = profile.Mechanics.Count + 1;
        string id;
        do
        {
            id = $"{prefix}_{index++}";
        }
        while (profile.Mechanics.Any(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)));
        return id;
    }

    private void DrawFileSettingsPanel()
    {
        if (_workingCopy is null)
        {
            return;
        }

        var enableTrig = _workingCopy.AutoSettings.EnableTriggers;
        if (ImGui.Checkbox("enable_triggers（突入時に自動でトリガー有効化）", ref enableTrig))
        {
            _workingCopy.AutoSettings.EnableTriggers = enableTrig;
            _dirty = true;
        }
        var autoRec = _workingCopy.AutoSettings.AutoRecord;
        if (ImGui.Checkbox("auto_record（突入時に自動で録画開始）", ref autoRec))
        {
            _workingCopy.AutoSettings.AutoRecord = autoRec;
            _dirty = true;
        }
        var showTl = _workingCopy.AutoSettings.ShowTimeline;
        if (ImGui.Checkbox("show_timeline（突入時にライブHUDを表示）", ref showTl))
        {
            _workingCopy.AutoSettings.ShowTimeline = showTl;
            _dirty = true;
        }
        var showPredicted = _workingCopy.AutoSettings.ShowPredictedCasts;
        if (ImGui.Checkbox("show_predicted_casts（録画ベースの予測キャストをタイムラインに表示）", ref showPredicted))
        {
            _workingCopy.AutoSettings.ShowPredictedCasts = showPredicted;
            _dirty = true;
        }
        var autoTel = _workingCopy.AutoSettings.ShowAutoTelegraphs;
        if (ImGui.Checkbox("show_auto_telegraphs（敵キャストの AoE 範囲を自動表示）", ref autoTel))
        {
            _workingCopy.AutoSettings.ShowAutoTelegraphs = autoTel;
            _dirty = true;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "ON にすると、敵キャストごとに Lumina の Action.EffectRange / CastType から\n" +
                "実寸の AoE 範囲を自動でミニマップ + 床塗りに描画する（学習不要、初見からそこそこ正確）。\n" +
                "・全体攻撃マーク済キャストは描画スキップ\n" +
                "・アリーナ半径の 90% 超の巨大 AoE は描画スキップ（画面真っ赤になるのを防止）\n" +
                "・PT 内のプレイヤーキャストは無視\n" +
                "・攻略登録で作った手動レイアウトとは独立して重ねて表示される\n" +
                "・床塗りだけ無効化したい場合は次の show_floor_paint を OFF に");
        }

        // 画面床塗り（Splatoon 風）の独立トグル：ミニマップは出したいが床塗りは画面が
        // うるさいから消したい、というユーザー向け。show_auto_telegraphs と AND 評価。
        ImGui.Indent();
        var showFloor = _workingCopy.AutoSettings.ShowFloorPaint;
        if (ImGui.Checkbox("show_floor_paint（床面の塗りつぶし AoE を画面に描画）", ref showFloor))
        {
            _workingCopy.AutoSettings.ShowFloorPaint = showFloor;
            _dirty = true;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Splatoon 風に床面に貼り付く塗りつぶし AoE を画面に描画する。\n" +
                "OFF にしてもミニマップ表示は維持される（show_auto_telegraphs に従う）。\n" +
                "「ボスやエフェクトが見えにくい」「画面に AoE が多すぎる」場合に OFF 推奨。");
        }
        ImGui.Unindent();

        var showAllEnemyCasts = _workingCopy.AutoSettings.ShowAllEnemyCasts;
        if (ImGui.Checkbox("show_all_enemy_casts（内部デバッグ用：不明AoEは描画しない）", ref showAllEnemyCasts))
        {
            _workingCopy.AutoSettings.ShowAllEnemyCasts = showAllEnemyCasts;
            _dirty = true;
        }

        var showAutoAttacks = _workingCopy.AutoSettings.ShowAutoAttacks;
        if (ImGui.Checkbox("show_auto_attacks（AA/即時アクションを表示）", ref showAutoAttacks))
        {
            _workingCopy.AutoSettings.ShowAutoAttacks = showAutoAttacks;
            _dirty = true;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("AA は Dalamud 側で action id が観測できた場合だけ表示します。うるさい場合は OFF 推奨です。");
        }

        ImGui.Spacing();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("予測アドバンス警告:");
        ImGui.SameLine();
        var warnEnabled = _workingCopy.AutoSettings.PredictAdvanceWarningSec is > 0;
        if (ImGui.Checkbox("##predict-warn-enable", ref warnEnabled))
        {
            _workingCopy.AutoSettings.PredictAdvanceWarningSec = warnEnabled ? AutoSettings.DefaultPredictAdvanceWarningSec : null;
            _dirty = true;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("録画から拾った各キャストについて、開始の何秒前に「次：〇〇」と TTS で先行通知するか。\n" +
                             "0 / OFF で無効。トリガー定義に同じ cast_id がある場合は重複通知しない。");
        }
        if (warnEnabled)
        {
            ImGui.SameLine();
            var warnSec = (float)(_workingCopy.AutoSettings.PredictAdvanceWarningSec ?? AutoSettings.DefaultPredictAdvanceWarningSec);
            ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
            if (ImGui.InputFloat("秒前##predict-warn-sec", ref warnSec))
            {
                _workingCopy.AutoSettings.PredictAdvanceWarningSec = warnSec <= 0 ? null : warnSec;
                _dirty = true;
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        DrawRaidWideMarkManagement();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled($"バージョン: {_workingCopy.Version}");
        var path = _triggerStore.GetFilePathForZone(_workingZone);
        if (path is not null)
        {
            ImGui.TextDisabled($"ファイル: {path}");
        }
    }

    /// <summary>
    /// 「全体攻撃にマーク」したキャストの管理。間違いマーク解除用。
    /// 現在編集中の TriggerFile からコンテンツ別マーカーを表示・解除。
    /// </summary>
    private void DrawRaidWideMarkManagement()
    {
        ImGui.TextUnformatted("全体攻撃マーク済みキャスト");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "このコンテンツ内で「全体攻撃にマーク」したキャストだけがここに溜まる。\n" +
                "マーク済みのキャストはミニマップに範囲が描画されなくなる（回避不能扱い）。\n" +
                "別コンテンツには影響しない。間違ってマークした場合は「解除」を押せば描画が戻る。");
        }
        if (_workingCopy is null)
        {
            return;
        }
        var raidWides = _workingCopy.RaidWideMarkers;
        if (raidWides.Count == 0)
        {
            ImGui.TextDisabled("マーク済みのキャストはまだありません。");
            ImGui.TextDisabled("（集計タブで cast を選んで「全体攻撃にマーク」を押すと追加されます）");
            return;
        }
        if (ImGui.BeginTable("##rw-table", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("キャスト ID", ImGuiTableColumnFlags.WidthFixed, 100f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("名前 / コール", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("検出元", ImGuiTableColumnFlags.WidthFixed, 140f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 80f * ImGuiHelpers.GlobalScale);
            ImGui.TableHeadersRow();
            for (var i = raidWides.Count - 1; i >= 0; i--)
            {
                var entry = raidWides[i];
                var key = string.IsNullOrWhiteSpace(entry.Id) ? "—" : entry.Id;
                ImGui.PushID($"rw-row-{key}");
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(key);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(entry.Callout ?? entry.Tts ?? entry.Name ?? "—");
                ImGui.TableNextColumn();
                var src = entry.Source switch
                {
                    "manual" => "手動",
                    "hp_correlation" => "HP 相関で自動",
                    _ => entry.Source ?? "—",
                };
                ImGui.TextDisabled(src);
                ImGui.TableNextColumn();
                if (ImGui.SmallButton("解除"))
                {
                    raidWides.RemoveAt(i);
                    _dirty = true;
                }
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
    }

    private string? _pendingRestorePath;

    private void DrawBackupsPanel(string zone)
    {
        var backupManager = _triggerStore.BackupManager;
        if (backupManager is null)
        {
            ImGui.TextDisabled("バックアップ機構が利用できません。");
            return;
        }

        ImGui.TextWrapped("ゾーン定義の保存（上書き）時には、直前の状態が自動でバックアップされます。" +
            "古いバックアップは保持上限を超えると自動削除されます。");
        ImGui.Spacing();

        var backups = backupManager.List(zone);
        ImGui.TextDisabled($"{backups.Count} 件のバックアップ");
        ImGui.Spacing();

        if (backups.Count == 0)
        {
            ImGui.TextWrapped("まだバックアップがありません。一度「保存」を行うとここに表示されます。");
            return;
        }

        if (ImGui.BeginTable("##backups-table", 4,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp |
            ImGuiTableFlags.ScrollY))
        {
            ImGui.TableSetupColumn("作成日時", ImGuiTableColumnFlags.WidthStretch, 2.0f);
            ImGui.TableSetupColumn("サイズ", ImGuiTableColumnFlags.WidthFixed, 80f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("ファイル", ImGuiTableColumnFlags.WidthStretch, 3.0f);
            ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 130f * ImGuiHelpers.GlobalScale);
            ImGui.TableHeadersRow();

            foreach (var backup in backups)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(backup.CreatedAtLocal.ToString("yyyy-MM-dd HH:mm:ss"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{backup.Size:N0} B");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(System.IO.Path.GetFileName(backup.Path));
                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"復元##{backup.Path}"))
                {
                    _pendingRestorePath = backup.Path;
                    ImGui.OpenPopup("restore-confirm");
                }
            }
            ImGui.EndTable();
        }

        if (ImGui.BeginPopupModal("restore-confirm", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped("このバックアップで現在の定義を上書きしますか？");
            ImGui.TextDisabled(_pendingRestorePath ?? string.Empty);
            ImGui.Spacing();
            ImGui.TextWrapped("（上書き前の現状もバックアップが取られます）");
            ImGui.Spacing();
            if (ImGui.Button("復元する"))
            {
                if (_pendingRestorePath is { } path)
                {
                    _triggerStore.RestoreFromBackup(zone, path);
                    LoadWorkingCopy(zone);
                }
                _pendingRestorePath = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("キャンセル"))
            {
                _pendingRestorePath = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
    }

    private void DrawNotesPanel()
    {
        if (_workingCopy is null)
        {
            return;
        }
        ImGui.TextWrapped("「ここで軽減」「ここで LB」を書いておくと、ライブタイムラインに表示されます。" +
            "時刻は秒で直接指定するか、「特定キャストに紐付け」で録画から自動解決させられます。" +
            "advance_warning_sec を設定するとその秒数前に TTS / オーバーレイで先行通知。");
        ImGui.Spacing();

        if (ImGui.Button("新規ノート##new-note"))
        {
            _workingCopy.Notes.Add(new TimelineNote
            {
                Id = $"note_{_workingCopy.Notes.Count + 1}",
                Time = 0,
                Label = "",
            });
            _dirty = true;
        }
        ImGui.SameLine();
        if (ImGui.Button("録画キャストから一括追加"))
        {
            ImGui.OpenPopup("note-from-cast-popup");
        }
        DrawNoteFromCastPopup();
        ImGui.Spacing();

        if (_workingCopy.Notes.Count == 0)
        {
            ImGui.TextDisabled("ノートがありません。「新規ノート」or「録画キャストから一括追加」で追加してください。");
            return;
        }

        if (ImGui.BeginTable("##notes-table", 7,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthStretch, 1.0f);
            ImGui.TableSetupColumn("時刻(s)", ImGuiTableColumnFlags.WidthFixed, 80f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("ラベル", ImGuiTableColumnFlags.WidthStretch, 2.0f);
            ImGui.TableSetupColumn("先行通知(s)", ImGuiTableColumnFlags.WidthFixed, 90f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("ロール", ImGuiTableColumnFlags.WidthFixed, 80f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("色", ImGuiTableColumnFlags.WidthFixed, 90f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 70f * ImGuiHelpers.GlobalScale);
            ImGui.TableHeadersRow();

            for (int i = 0; i < _workingCopy.Notes.Count; i++)
            {
                var note = _workingCopy.Notes[i];
                ImGui.PushID($"note-{i}");
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                var id = note.Id;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("##id", ref id, 32)) { note.Id = id; _dirty = true; }

                ImGui.TableNextColumn();
                if (note.AttachedTo is not null)
                {
                    // 紐付け中：時刻入力は無効化、cast_id を表示し編集ボタンで切替
                    var attachLabel = note.AttachedTo.CastId ?? note.AttachedTo.CastName ?? "—";
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(new Vector4(0.4f, 0.85f, 1f, 1f), $"📌 {attachLabel}");
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("特定キャストに紐付け中。クリックすると解除");
                    if (ImGui.IsItemClicked())
                    {
                        note.AttachedTo = null;
                        _dirty = true;
                    }
                }
                else
                {
                    var time = (float)note.Time;
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.InputFloat("##time", ref time, 1.0f, 5.0f, "%.1f")) { note.Time = time; _dirty = true; }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("戦闘相対秒。録画キャストに紐付けたい時はラベル右横の 📎 ボタン");
                }

                ImGui.TableNextColumn();
                var label = note.Label;
                ImGui.SetNextItemWidth(-32f * ImGuiHelpers.GlobalScale);
                if (ImGui.InputText("##label", ref label, 128)) { note.Label = label; _dirty = true; }
                ImGui.SameLine();
                if (ImGui.SmallButton("📎"))
                {
                    _attachNoteIndex = i;
                    ImGui.OpenPopup("note-attach-popup");
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("録画されたキャストに紐付けて時刻を自動解決");

                ImGui.TableNextColumn();
                var warn = (float)(note.AdvanceWarningSec ?? 0);
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputFloat("##warn", ref warn, 0.5f, 1.0f, "%.1f"))
                {
                    note.AdvanceWarningSec = warn <= 0 ? null : warn;
                    _dirty = true;
                }

                ImGui.TableNextColumn();
                var role = note.Role ?? string.Empty;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("##role", ref role, 16))
                {
                    note.Role = string.IsNullOrEmpty(role) ? null : role;
                    _dirty = true;
                }

                ImGui.TableNextColumn();
                var color = note.Color ?? string.Empty;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("##color", ref color, 8))
                {
                    note.Color = string.IsNullOrEmpty(color) ? null : color;
                    _dirty = true;
                }

                ImGui.TableNextColumn();
                if (ImGui.SmallButton("削除"))
                {
                    _workingCopy.Notes.RemoveAt(i);
                    _dirty = true;
                    ImGui.PopID();
                    break;
                }

                ImGui.PopID();
            }
            ImGui.EndTable();
        }

        DrawNoteAttachPopup();
    }

    /// <summary>
    /// ノートを録画されたキャストに紐付けるためのピッカー popup。
    /// </summary>
    private void DrawNoteAttachPopup()
    {
        ImGui.SetNextWindowSize(new Vector2(500f * ImGuiHelpers.GlobalScale, 400f * ImGuiHelpers.GlobalScale));
        if (!ImGui.BeginPopup("note-attach-popup"))
        {
            return;
        }
        if (_attachNoteIndex < 0 || _workingCopy is null || _attachNoteIndex >= _workingCopy.Notes.Count)
        {
            ImGui.EndPopup();
            return;
        }
        var note = _workingCopy.Notes[_attachNoteIndex];
        ImGui.TextWrapped("録画から拾ったキャストにこのノートを紐付けます。" +
                          "選択するとノートの時刻が自動で「キャスト開始の相対秒」に追従します。");
        ImGui.Spacing();

        var agg = _recordingScanner.Aggregate(_workingZone);
        var filtered = ApplyEventFilters(agg);
        if (ImGui.BeginChild("##attach-list", new Vector2(-1, 320f * ImGuiHelpers.GlobalScale), true))
        {
            foreach (var ev in filtered)
            {
                if (ev.Key.Type != "cast_start") continue;
                var label = $"[{ev.FirstSeenSeconds:0.0}s]  {ev.Key.Name ?? "?"}  ({ev.Key.Id ?? "—"})  ×{ev.Count}";
                if (ImGui.Selectable(label))
                {
                    note.AttachedTo = new MatchCondition
                    {
                        CastId = ev.Key.Id,
                        CastName = ev.Key.Name,
                    };
                    _dirty = true;
                    ImGui.CloseCurrentPopup();
                }
            }
        }
        ImGui.EndChild();

        if (ImGui.Button("キャンセル##attach-cancel"))
        {
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    /// <summary>
    /// 録画キャストから一括ノート生成 popup。チェックを入れたものだけノートにする。
    /// </summary>
    private readonly HashSet<string> _bulkSelectedCastIds = new(StringComparer.OrdinalIgnoreCase);
    private float _bulkAdvanceWarn = 5f;

    private void DrawNoteFromCastPopup()
    {
        ImGui.SetNextWindowSize(new Vector2(560f * ImGuiHelpers.GlobalScale, 480f * ImGuiHelpers.GlobalScale));
        if (!ImGui.BeginPopupModal("note-from-cast-popup", ImGuiWindowFlags.NoCollapse))
        {
            return;
        }
        if (_workingCopy is null) { ImGui.EndPopup(); return; }

        ImGui.TextWrapped("録画されたキャストから一括でノートを生成します。チェックを入れたキャストごとに" +
                          "「📌 紐付けノート」が作成されます（時刻は自動解決）。");
        ImGui.Spacing();

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("先行通知秒数:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
        ImGui.InputFloat("##bulk-warn", ref _bulkAdvanceWarn, 0.5f, 1.0f, "%.1f");
        ImGui.Spacing();

        var agg = _recordingScanner.Aggregate(_workingZone);
        var filtered = ApplyEventFilters(agg);
        if (ImGui.BeginChild("##bulk-list", new Vector2(-1, 340f * ImGuiHelpers.GlobalScale), true))
        {
            foreach (var ev in filtered)
            {
                if (ev.Key.Type != "cast_start") continue;
                var key = ev.Key.Id ?? ev.Key.Name ?? "?";
                var checkedNow = _bulkSelectedCastIds.Contains(key);
                if (ImGui.Checkbox($"[{ev.FirstSeenSeconds:0.0}s]  {ev.Key.Name ?? "?"}  ({ev.Key.Id ?? "—"})  ×{ev.Count}##bulk-{key}",
                        ref checkedNow))
                {
                    if (checkedNow) _bulkSelectedCastIds.Add(key);
                    else _bulkSelectedCastIds.Remove(key);
                }
            }
        }
        ImGui.EndChild();

        if (ImGui.Button($"{_bulkSelectedCastIds.Count} 件のノートを生成", new Vector2(220f * ImGuiHelpers.GlobalScale, 0)))
        {
            foreach (var ev in filtered)
            {
                if (ev.Key.Type != "cast_start") continue;
                var key = ev.Key.Id ?? ev.Key.Name ?? "?";
                if (!_bulkSelectedCastIds.Contains(key)) continue;
                var label = ev.Key.Name ?? key;
                _workingCopy.Notes.Add(new TimelineNote
                {
                    Id = $"note_attached_{key.Replace("0x", "").Replace("#", "").ToLowerInvariant()}",
                    Label = label,
                    AttachedTo = new MatchCondition { CastId = ev.Key.Id, CastName = ev.Key.Name },
                    AdvanceWarningSec = _bulkAdvanceWarn > 0 ? _bulkAdvanceWarn : null,
                    Color = "#FCD34D",
                });
            }
            _dirty = true;
            _bulkSelectedCastIds.Clear();
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("キャンセル##bulk-cancel"))
        {
            _bulkSelectedCastIds.Clear();
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private static TriggerFile Clone(TriggerFile src)
    {
        // 編集中は元データを汚さないようディープコピー（System.Text.Json 経由で簡易に）
        var json = TriggerSerializer.Serialize(src);
        return System.Text.Json.JsonSerializer.Deserialize<TriggerFile>(json,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = false,
            }) ?? new TriggerFile();
    }

    private static readonly string[] EventTypes =
    {
        "cast_start", "cast_complete", "cast_cancel",
        "action_used",
        "status_gain", "status_lose", "status_update",
        "hp_change", "zone_change",
        "combat_start", "combat_end",
        "object_appear", "object_disappear", "timeline_elapsed",
    };

    private static readonly string[] ActionTypes =
    {
        "tts", "wav", "overlay_text", "overlay_corner_text", "timer_bar",
        "chat_echo", "direction_call", "screen_arrow", "field_marker",
        "proximity_feedback", "set_variable", "chain_trigger",
        "store_position", "arena_view",
    };

    private static readonly string[] ArenaViewGimmicks =
    {
        "outer_ring", "inner_circle", "scatter", "stack", "cone",
    };

    private static readonly string[] ArenaViewDirections =
    {
        "N", "NE", "E", "SE", "S", "SW", "W", "NW",
    };

    private static readonly string[] DirectionFormats =
    {
        "cardinal", "cardinal_jp", "clock", "relative_jp", "degrees",
    };

    private static readonly string[] FieldShapes =
    {
        "circle", "square", "x_mark", "arrow",
    };

    private static readonly string[] SafeZoneMethods =
    {
        "fixed",
        "boss_relative",
        "marker_relative",
        "arena_center_relative",
        "inverse_of_telegraph",
        "party_member_relative",
        "find_actor_with_status",
        "find_actor_without_status",
        "find_actor_not_casting",
        "find_actor_by_distance",
        "midpoint",
        "between_actors",
        "line_perpendicular",
        "telegraph_gap",
        "intersection",
        "stored_position",
    };

    /// <summary>arena_view のギミック種類ごとの説明文（日本語）。</summary>
    private static string GimmickTooltip(string gimmick) => gimmick switch
    {
        "outer_ring" => "ボスから遠いほど危険、中央に安置の緑丸を描画。\n例：「無の肥大」「外周回避」のような全体 AoE",
        "inner_circle" => "ボス周囲が危険、外周が安置。\n例：「サークル AoE」「中央回避」のようなボス中心 AoE",
        "scatter" => "4 方向（北東南西）に散開ポジを描画。\n例：散開デバフ、ターゲット指定 AoE 系",
        "stack" => "中央集合マーカー。\n例：シェアダメージ、テラスト系",
        "cone" => "指定方向への扇形危険ゾーン。\n方向と扇の角度（90 度等）を別途設定。",
        _ => gimmick,
    };

    /// <summary>safe_zone（SafeZoneCalculation）編集ヘルパ。method 選択 + raw JSON params。</summary>
    private void DrawSafeZoneSubEditor(ActionDefinition action)
    {
        ImGui.Spacing();
        var hasZone = action.SafeZone is not null;
        if (ImGui.CollapsingHeader($"安置計算 (safe_zone){(hasZone ? "  ✓" : "")}##sz"))
        {
            ImGui.Indent(12f);

            if (!hasZone)
            {
                if (ImGui.Button("安置計算を追加##sz-add"))
                {
                    action.SafeZone = new SafeZoneCalculation { Method = "fixed" };
                    _dirty = true;
                }
                ImGui.TextDisabled("F4-F7 の 16 種プリセットから選んで世界座標を計算します。");
            }
            else
            {
                var sz = action.SafeZone!;
                var method = sz.Method;
                var mIdx = Array.IndexOf(SafeZoneMethods, method);
                if (mIdx < 0) mIdx = 0;
                var methodLabels = Localization.LocalizeAll(SafeZoneMethods, Localization.SafeZoneMethod);
                ImGui.SetNextItemWidth(360f * ImGuiHelpers.GlobalScale);
                if (ImGui.Combo("計算方式##sz-method", ref mIdx, methodLabels, methodLabels.Length))
                {
                    sz.Method = SafeZoneMethods[mIdx];
                    _dirty = true;
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(SafeZoneMethodTooltip(sz.Method));

                ImGui.TextDisabled("params (JSON):");
                var paramsBuf = SerializeParams(sz);
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputTextMultiline("##sz-params", ref paramsBuf, 4096,
                    new Vector2(-1, 80f * ImGuiHelpers.GlobalScale)))
                {
                    if (TryParseParams(paramsBuf, out var newParams, out var err))
                    {
                        sz.Params = newParams;
                        _szParseError = null;
                        _dirty = true;
                    }
                    else
                    {
                        _szParseError = err;
                    }
                }
                if (!string.IsNullOrEmpty(_szParseError))
                {
                    ImGui.TextColored(new Vector4(1f, 0.5f, 0.5f, 1f), $"JSON エラー: {_szParseError}");
                }
                if (ImGui.SmallButton("削除##sz-remove"))
                {
                    action.SafeZone = null;
                    _dirty = true;
                }
            }

            ImGui.Unindent(12f);
        }
    }

    private string? _szParseError;

    /// <summary>
    /// 集計イベントをフィルタする。「自分・PT のイベントを隠す」「status を隠す」の組合せ。
    /// PT 名は録画 meta から取得する。
    /// </summary>
    /// <summary>
    /// タイムライン表示専用の dedup。同じ Type かつ同じ Name（=技名）が ±1 秒以内に
    /// 並んでいたら 1 件にまとめる。FFXIV では「テレグラフ用 cast_id」と「実ダメージ用
    /// cast_id」が同時刻に独立して飛んでくることがあり、見た目の重複ノイズになるため。
    /// 観測回数が多い方を残す。
    /// </summary>
    private static IReadOnlyList<Recording.AggregatedEvent> DedupSameNameSameTime(
        IReadOnlyList<Recording.AggregatedEvent> events)
    {
        if (events.Count <= 1) return events;
        var sorted = events.OrderBy(e => e.FirstSeenSeconds).ToArray();
        var consumed = new bool[sorted.Length];
        var result = new List<Recording.AggregatedEvent>(sorted.Length);
        const double Window = 1.0;
        for (var i = 0; i < sorted.Length; i++)
        {
            if (consumed[i]) continue;
            var head = sorted[i];
            var bestIdx = i;
            var bestCount = head.Count;
            for (var j = i + 1; j < sorted.Length; j++)
            {
                if (consumed[j]) continue;
                var c = sorted[j];
                if (c.FirstSeenSeconds - head.FirstSeenSeconds > Window) break;
                if (!string.Equals(c.Key.Type, head.Key.Type, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrEmpty(c.Key.Name) || string.IsNullOrEmpty(head.Key.Name)) continue;
                if (!string.Equals(c.Key.Name, head.Key.Name, StringComparison.OrdinalIgnoreCase)) continue;
                consumed[j] = true;
                if (c.Count > bestCount)
                {
                    bestCount = c.Count;
                    bestIdx = j;
                }
            }
            result.Add(sorted[bestIdx]);
        }
        return result;
    }

    private IReadOnlyList<Recording.AggregatedEvent> ApplyEventFilters(Recording.AggregatedEvents agg)
    {
        HashSet<string>? party = null;
        if (_hideSelfEvents)
        {
            var members = _recordingScanner.ListPartyMembers(_workingZone);
            if (members.Count > 0)
            {
                party = new HashSet<string>(members, StringComparer.OrdinalIgnoreCase);
            }
        }

        var battleCount = Math.Max(1, agg.BattleCount);
        // 旧 EnvNoisePerBattleThreshold は object_appear を「閾値以下は通す」設計だったが、
        // ボス名 / 環境物名が「その他」レーンに残るノイズ源だったので、無条件除外に切り替え。

        var result = new List<Recording.AggregatedEvent>(agg.Events.Count);
        foreach (var ev in agg.Events)
        {
            // cast_complete / cast_cancel は cast_start と同じキャストの「終了通知」なので
            // 集計ビューでは重複ノイズになる。常に隠す。
            if (ev.Key.Type == "cast_complete" || ev.Key.Type == "cast_cancel")
            {
                continue;
            }
            if (_hideStatusEvents && (ev.Key.Type == "status_gain" || ev.Key.Type == "status_lose" ||
                                       ev.Key.Type == "status_update"))
            {
                continue;
            }
            // 環境ノイズフィルタ：unknown 型と object_appear/disappear を隠す
            // 旧実装は object_appear を「閾値（5/戦）以下なら通す」設計だったが、ボス名 / 環境物名
            // （ゾディアーク／ケツァクワァトル／ベヒーモス／脱出地点／秘紋等）が「その他」レーンに
            // ノイズとして残る原因だった。閾値判定を廃止し、フィルタ ON 時は **全件除外**。
            // ユーザーが見たい場合は環境ノイズ表示チェックを外すと出る（オプトイン化）。
            if (_hideEnvironmentalNoise)
            {
                if (ev.Key.Type == "unknown" ||
                    ev.Key.Type == "object_appear" ||
                    ev.Key.Type == "object_disappear")
                {
                    continue;
                }
            }
            // IsPartySource フィルタ：RecordingAggregationReader が「PT 関連」とマークした
            // イベントは PC スキル / ペットスキル（クラノウス・エイドロン等）なので除外。
            // 旧実装は名前照合のみだったため、ペット名が PT メンバー名と一致せず漏れていた。
            if (ev.IsPartySource)
            {
                continue;
            }
            if (party is not null)
            {
                if (!string.IsNullOrEmpty(ev.Key.Target) && party.Contains(ev.Key.Target!))
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(ev.Key.Source) && party.Contains(ev.Key.Source!))
                {
                    continue;
                }
            }
            result.Add(ev);
        }
        return result;
    }

    private static string SafeZoneMethodTooltip(string method) => method switch
    {
        "fixed" => "params 例: { x: 100, y: 0, z: 100 }\n固定の世界座標を安置とする",
        "boss_relative" => "params 例: { offset: { x: 0, z: 15 } }\nボス位置からの相対オフセット（南に 15m など）",
        "marker_relative" => "params 例: { marker: \"A\" }\nフィールドマーカー（A/B/C/D/1/2/3/4）基準",
        "arena_center_relative" => "params 例: { offset: { x: 0, z: -15 } }\nアリーナ中心からの相対",
        "inverse_of_telegraph" => "params 例: { telegraph: { shape: \"fan\", angle_deg: 180 } }\n敵の AoE の反対側を安置とする（最も使う）",
        "party_member_relative" => "params 例: { role: \"tank\", index: 0, offset: { z: 5 } }\n指定ロールの PT メンバー基準",
        "find_actor_with_status" => "params 例: { status_id: 1234, offset: { z: 5 } }\n特定ステータスを持つ敵基準",
        "find_actor_without_status" => "params 例: { status_id: 1234 }\n特定ステータスを持たない敵基準",
        "find_actor_not_casting" => "params 例: { offset: { z: 0 } }\nキャストしていない敵基準",
        "find_actor_by_distance" => "params 例: { side: \"nearest\" }\n自分から最も近い／遠い敵基準",
        "midpoint" => "params 例: { actors: [\"敵A\", \"敵B\"] }\n2 アクターの中点",
        "between_actors" => "params 例: { actor_a: \"X\", actor_b: \"Y\", fraction: 0.5 }\n2 アクターを結ぶ線分上の指定割合の点",
        "line_perpendicular" => "params 例: { actors: [\"X\", \"Y\"], distance: 10 }\n2 点を結ぶ線への垂線方向",
        "telegraph_gap" => "params 例: { telegraphs: [...] }\n複数 AoE の隙間",
        "intersection" => "params 例: { calculations: [calc1, calc2] }\n複数計算の交差点（AND）",
        "stored_position" => "params 例: { name: \"slot1\" }\n以前 store_position で保存した位置（P4）",
        _ => method,
    };

    private static string SerializeParams(SafeZoneCalculation sz)
    {
        if (sz.Params is null || sz.Params.Count == 0)
        {
            return "{}";
        }
        try
        {
            return JsonSerializer.Serialize(sz.Params, new JsonSerializerOptions
            {
                WriteIndented = false,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        }
        catch
        {
            return "{}";
        }
    }

    private static bool TryParseParams(string text, out Dictionary<string, JsonElement>? result, out string? error)
    {
        result = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text) || text.Trim() == "{}")
        {
            return true;
        }
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "object でなければなりません";
                return false;
            }
            var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.Clone();
            }
            result = dict;
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
