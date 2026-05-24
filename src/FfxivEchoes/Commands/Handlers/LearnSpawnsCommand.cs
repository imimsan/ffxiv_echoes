using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FfxivEchoes.Triggers;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Commands.Handlers;

/// <summary>
/// 録画から「Cast → N 秒後に Object 出現」パターンを学習し、
/// <see cref="StrategyProfile.PredictedObjectSpawns"/> を更新するコマンド。
/// 使い方：<c>/echoes learn-spawns [zone]</c> （zone 省略時は現在ゾーン）
/// </summary>
public sealed class LearnSpawnsCommand : ICommandHandler
{
    public string Verb => "learn-spawns";
    public string Usage => "learn-spawns [zone] [force]";
    public string Description => "録画から「Cast → Object 出現」予告データを学習し、攻略登録に保存。force で既存自動学習を消去して再学習";

    private readonly PredictedObjectSpawnLearner _learner;
    private readonly TriggerStore _store;
    private readonly IClientState _clientState;
    private readonly IDataManager _dataManager;
    private readonly IChatGui _chat;
    private readonly IPluginLog _log;

    public LearnSpawnsCommand(
        PredictedObjectSpawnLearner learner,
        TriggerStore store,
        IClientState clientState,
        IDataManager dataManager,
        IChatGui chat,
        IPluginLog log)
    {
        _learner = learner;
        _store = store;
        _clientState = clientState;
        _dataManager = dataManager;
        _chat = chat;
        _log = log;
    }

    public void Execute(string args)
    {
        // "force" / "--force" は既存自動学習を破棄して再学習。残りトークンを zone 名として結合。
        var tokens = (args ?? string.Empty)
            .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .ToList();
        var force = tokens.RemoveAll(t =>
            string.Equals(t, "force", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t, "--force", StringComparison.OrdinalIgnoreCase)) > 0;
        var zone = string.Join(" ", tokens).Trim();
        if (string.IsNullOrWhiteSpace(zone))
        {
            zone = ResolveCurrentZoneName();
        }

        if (string.IsNullOrWhiteSpace(zone) || zone == "Unknown")
        {
            _chat.PrintError("[FFXIV Echoes] learn-spawns: ゾーン名が取得できません。引数で明示してください。");
            return;
        }

        var file = _store.GetByZone(zone);
        if (file is null)
        {
            _chat.PrintError($"[FFXIV Echoes] learn-spawns: '{zone}' のトリガーファイルが見つかりません");
            return;
        }

        var profile = file.StrategyProfiles.FirstOrDefault(p => p.Enabled)
                    ?? file.StrategyProfiles.FirstOrDefault();
        if (profile is null)
        {
            _chat.PrintError($"[FFXIV Echoes] learn-spawns: '{zone}' に有効な StrategyProfile がありません");
            return;
        }

        // force：既存の自動学習 (Source != "manual") を破棄してから再学習
        if (force)
        {
            var before = profile.PredictedObjectSpawns?.Count ?? 0;
            profile.PredictedObjectSpawns = (profile.PredictedObjectSpawns ?? new List<PredictedObjectSpawn>())
                .Where(s => string.Equals(s.Source, "manual", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var removed = before - profile.PredictedObjectSpawns.Count;
            _chat.Print($"[FFXIV Echoes] force: {removed} 件の自動学習を削除（手動分は保持）");
        }

        _chat.Print($"[FFXIV Echoes] learn-spawns: {zone} の録画を解析中...");

        var learned = _learner.LearnFromRecordings(zone, profile.ArenaCenterX, profile.ArenaCenterZ, profile.ObjectAoeRules);
        if (learned.Count == 0)
        {
            _chat.Print($"[FFXIV Echoes] learn-spawns: 学習結果なし (録画数不足 or パターン未検出)");
            return;
        }

        // 既存 spawn とのマージ：Source="manual" は保持、自動学習由来は上書き
        var existing = profile.PredictedObjectSpawns ?? new List<PredictedObjectSpawn>();
        var manualSpawns = existing.Where(s => string.Equals(s.Source, "manual", StringComparison.OrdinalIgnoreCase)).ToList();

        var merged = new List<PredictedObjectSpawn>(manualSpawns);
        foreach (var s in learned)
        {
            // 手動エントリと ID 衝突しないものだけ追加
            if (!manualSpawns.Any(m => string.Equals(m.Id, s.Id, StringComparison.OrdinalIgnoreCase)))
            {
                merged.Add(s);
            }
        }

        profile.PredictedObjectSpawns = merged;

        // ObjectAoeRule reconcile: 同 ObjectName + DataId のルールがあり、source が
        // recording/recording_action 系なら、predict 側の Lumina 学習値 (radius/shape) で
        // 上書きする。両者は同じ Lumina action を引いているはずだが、過去バージョンで学習
        // された ObjectAoeRule が古い値 (例: radius_m=15 で実際は 6m) のまま残ることがあり、
        // 実出現後の床描画が誤サイズになる原因 (T1 §2 症状C / §4 優先度1)。
        // source=manual / dictionary は触らない (ユーザー判断や辞書値を尊重)。
        var reconciledCount = ReconcileObjectAoeRulesFromPredictedSpawns(profile, merged);

        try
        {
            _store.SaveZone(zone, file);
            if (reconciledCount > 0)
            {
                _chat.Print($"[FFXIV Echoes] object_aoe_rules の radius/shape を {reconciledCount} 件 reconcile（手動・辞書は保護）");
            }
            _chat.Print(
                $"[FFXIV Echoes] learn-spawns: {zone} を更新。学習={learned.Count} 手動保持={manualSpawns.Count} 合計={merged.Count}");
            _log.Information(
                "[FfxivEchoes] LearnSpawnsCommand: zone={Zone} learned={Learned} manual={Manual} merged={Merged}",
                zone, learned.Count, manualSpawns.Count, merged.Count);

            foreach (var s in learned)
            {
                var shapeDesc = s.Shape switch
                {
                    "donut" => $"donut {s.RadiusM:F1}m / inner {s.InnerRadiusM ?? 0:F1}m",
                    "rect" => $"rect {s.RadiusM:F1}m × {s.HalfWidthM ?? 0:F1}m",
                    "cone" => $"cone {s.RadiusM:F1}m / {s.FanDeg ?? 0:F0}°",
                    _ => $"{s.Shape} r={s.RadiusM:F1}m",
                };
                var posDesc = s.Positions.Count > 0
                    ? $"{s.Positions.Count}pts (e.g. {s.Positions[0].X:F1},{s.Positions[0].Z:F1})"
                    : "no positions";
                _chat.Print(
                    $"  • {s.TriggerCastName ?? "?"} → {s.ObjectName}(id={s.ObjectDataId?.ToString() ?? "?"}) ×{s.ObservedSpawnCount}");
                _chat.Print(
                    $"    delay={s.DelaySec:F1}s, lead={s.LeadTimeSec:F1}s, fire@cast+{s.DelaySec - s.LeadTimeSec:F1}s");
                _chat.Print(
                    $"    shape={shapeDesc}, {posDesc}, conf={s.Confidence:F2}, stable={s.IsPositionStable}");
            }
        }
        catch (Exception ex)
        {
            _chat.PrintError($"[FFXIV Echoes] learn-spawns: 保存失敗 - {ex.Message}");
            _log.Error(ex, "[FfxivEchoes] LearnSpawnsCommand: save 失敗 zone={Zone}", zone);
        }
    }

    /// <summary>
    /// 学習済み PredictedObjectSpawn の (radius/inner/shape) を、同名同 DataId の
    /// object_aoe_rules に反映する。source=recording/recording_action のみ対象。
    /// 手動編集 (manual) と辞書 (dictionary) は触らない。
    /// 戻り値：更新された ObjectAoeRule の件数。
    /// </summary>
    public static int ReconcileObjectAoeRulesFromPredictedSpawns(
        StrategyProfile profile,
        IReadOnlyList<PredictedObjectSpawn> spawns)
    {
        if (profile.ObjectAoeRules is null || profile.ObjectAoeRules.Count == 0) return 0;
        if (spawns is null || spawns.Count == 0) return 0;

        var updated = 0;
        foreach (var rule in profile.ObjectAoeRules)
        {
            // 手動編集と辞書は保護
            var srcLower = (rule.Source ?? "").ToLowerInvariant();
            if (srcLower == "manual" || srcLower == "dictionary") continue;
            if (string.IsNullOrWhiteSpace(rule.ObjectName)) continue;

            // 同名 + (DataId 一致 or rule の DataId が null) の最高信頼 spawn を選ぶ
            var spawn = spawns
                .Where(s => string.Equals(s.ObjectName?.Trim(), rule.ObjectName.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                .Where(s => rule.DataId is null || rule.DataId == 0 || s.ObjectDataId == rule.DataId)
                .OrderByDescending(s => s.Confidence)
                .FirstOrDefault();
            if (spawn is null) continue;
            if (spawn.RadiusM <= 0.1) continue;  // 学習失敗 spawn は無視

            // 変更があれば反映
            var changed = false;
            if (!string.Equals(rule.Shape, spawn.Shape, StringComparison.OrdinalIgnoreCase))
            {
                rule.Shape = spawn.Shape;
                changed = true;
            }
            if (Math.Abs(rule.RadiusM - spawn.RadiusM) > 0.05)
            {
                rule.RadiusM = spawn.RadiusM;
                changed = true;
            }
            if (NotNearlyEqual(rule.InnerRadiusM, spawn.InnerRadiusM, 0.05))
            {
                rule.InnerRadiusM = spawn.InnerRadiusM;
                changed = true;
            }
            if (changed) updated++;
        }
        return updated;
    }

    private static bool NotNearlyEqual(double? a, double? b, double tolerance)
    {
        if (a is null && b is null) return false;
        if (a is null || b is null) return true;
        return Math.Abs(a.Value - b.Value) > tolerance;
    }

    private string ResolveCurrentZoneName()
    {
        try
        {
            var territoryId = _clientState.TerritoryType;
            if (territoryId == 0) return "Unknown";
            var sheet = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
            if (sheet.TryGetRow(territoryId, out var row))
            {
                var name = row.PlaceName.Value.Name.ToString();
                return string.IsNullOrEmpty(name) ? $"Territory#{territoryId}" : name;
            }
        }
        catch { }
        return "Unknown";
    }
}
