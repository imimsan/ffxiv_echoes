using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Triggers;

/// <summary>
/// トリガー定義のエクスポート（exports/ ディレクトリへ書き出し）と
/// インポート（imports/ ディレクトリから読み込み + コンフリクト解決）を提供。
/// SPEC.md §12。
/// </summary>
public sealed class TriggerExportImport
{
    public const string ExportsDirName = "exports";
    public const string ImportsDirName = "imports";

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly TriggerStore _store;
    private readonly IPluginLog _log;

    public TriggerExportImport(IDalamudPluginInterface pluginInterface, TriggerStore store, IPluginLog log)
    {
        _pluginInterface = pluginInterface;
        _store = store;
        _log = log;
    }

    public string ExportsDirectory => Path.Combine(_pluginInterface.ConfigDirectory.FullName, ExportsDirName);
    public string ImportsDirectory => Path.Combine(_pluginInterface.ConfigDirectory.FullName, ImportsDirName);

    public string Export(string zone)
    {
        var file = _store.GetByZone(zone)
            ?? throw new InvalidOperationException($"Zone '{zone}' のトリガー定義が存在しません");

        Directory.CreateDirectory(ExportsDirectory);
        var fileName = $"{Sanitize(zone)}_{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json";
        var path = Path.Combine(ExportsDirectory, fileName);
        TriggerSerializer.WriteToFile(file, path);
        _log.Information("[FfxivEchoes] エクスポート：{Path}", path);
        return path;
    }

    public IReadOnlyList<ImportCandidate> ListImports()
    {
        if (!Directory.Exists(ImportsDirectory))
        {
            try
            {
                Directory.CreateDirectory(ImportsDirectory);
            }
            catch
            {
                return Array.Empty<ImportCandidate>();
            }
            return Array.Empty<ImportCandidate>();
        }

        var list = new List<ImportCandidate>();
        foreach (var path in Directory.EnumerateFiles(ImportsDirectory, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(path);
                var file = JsonSerializer.Deserialize<TriggerFile>(json,
                    new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (file is null || string.IsNullOrEmpty(file.Zone))
                {
                    continue;
                }
                var existing = _store.GetByZone(file.Zone);
                list.Add(new ImportCandidate(
                    Path: path,
                    File: file,
                    HasConflict: existing is not null,
                    ExistingTriggerCount: existing?.Triggers.Count ?? 0));
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[FfxivEchoes] インポート候補の読込失敗：{Path}", path);
            }
        }
        return list.OrderBy(c => c.File.Zone, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void Import(ImportCandidate candidate, ImportConflictResolution resolution)
    {
        var zone = candidate.File.Zone;
        var existing = _store.GetByZone(zone);

        if (existing is not null && resolution == ImportConflictResolution.Skip)
        {
            _log.Information("[FfxivEchoes] インポートスキップ：{Zone}", zone);
            return;
        }

        TriggerFile toSave;
        if (existing is null || resolution == ImportConflictResolution.Overwrite)
        {
            toSave = candidate.File;
        }
        else
        {
            // Merge：既存の triggers に、import 側で ID 重複しないものだけ追加
            toSave = existing;
            var existingIds = new HashSet<string>(
                existing.Triggers.Select(t => t.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var t in candidate.File.Triggers)
            {
                if (!existingIds.Contains(t.Id))
                {
                    existing.Triggers.Add(t);
                    existingIds.Add(t.Id);
                }
            }
            // sync_points / ignored_events も同様に ID 重複なしで追加
            var existingSyncIds = new HashSet<string>(
                existing.SyncPoints.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var sp in candidate.File.SyncPoints)
            {
                if (!existingSyncIds.Contains(sp.Id))
                {
                    existing.SyncPoints.Add(sp);
                    existingSyncIds.Add(sp.Id);
                }
            }
        }

        _store.SaveZone(zone, toSave);
        _log.Information("[FfxivEchoes] インポート完了：{Zone}（{Resolution}）", zone, resolution);
    }

    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "Unknown";
        }
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }
        return sb.ToString();
    }
}

public sealed record ImportCandidate(
    string Path,
    TriggerFile File,
    bool HasConflict,
    int ExistingTriggerCount);

public enum ImportConflictResolution
{
    Skip,
    Overwrite,
    Merge,
}
