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
    public string Usage => "learn-spawns [zone]";
    public string Description => "録画から「Cast → Object 出現」予告データを学習し、攻略登録に保存";

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
        var zone = args.Trim();
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

        try
        {
            _store.SaveZone(zone, file);
            _chat.Print(
                $"[FFXIV Echoes] learn-spawns: {zone} を更新。学習={learned.Count} 手動保持={manualSpawns.Count} 合計={merged.Count}");
            _log.Information(
                "[FfxivEchoes] LearnSpawnsCommand: zone={Zone} learned={Learned} manual={Manual} merged={Merged}",
                zone, learned.Count, manualSpawns.Count, merged.Count);

            foreach (var s in learned)
            {
                _chat.Print(
                    $"  • {s.TriggerCastName ?? "?"} → {s.ObjectName} ×{s.ObservedSpawnCount} " +
                    $"(delay={s.DelaySec:F1}s, confidence={s.Confidence:F2}, stable={s.IsPositionStable})");
            }
        }
        catch (Exception ex)
        {
            _chat.PrintError($"[FFXIV Echoes] learn-spawns: 保存失敗 - {ex.Message}");
            _log.Error(ex, "[FfxivEchoes] LearnSpawnsCommand: save 失敗 zone={Zone}", zone);
        }
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
