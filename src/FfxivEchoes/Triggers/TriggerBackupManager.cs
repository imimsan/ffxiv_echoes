using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Dalamud.Plugin.Services;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Triggers;

/// <summary>
/// トリガー定義の自動バックアップと復元（SPEC.md §2.3 / §7.4 / M10）。
/// </summary>
/// <remarks>
/// バックアップ先：<c>{ConfigDirectory}/triggers_backup/{zone}/{datetime}.json</c>
/// 保存（上書き）の直前に呼ばれ、既存ファイルの状態を timestamp 付きで複製する。
/// </remarks>
public sealed class TriggerBackupManager
{
    public const string BackupDirName = "triggers_backup";

    private readonly string _backupRoot;
    private readonly IPluginLog _log;
    private readonly int _retention;

    public TriggerBackupManager(string configDirectory, IPluginLog log, int retentionPerZone = 50)
    {
        _backupRoot = Path.Combine(configDirectory, BackupDirName);
        _log = log;
        _retention = retentionPerZone;
    }

    public string BackupRoot => _backupRoot;

    /// <summary>指定ゾーンの現在の定義をバックアップとして保存する。</summary>
    public string CreateBackup(string zoneName, TriggerFile file)
    {
        var dir = Path.Combine(_backupRoot, Sanitize(zoneName));
        Directory.CreateDirectory(dir);

        var fileName = $"{DateTimeOffset.UtcNow.ToLocalTime():yyyy-MM-dd_HH-mm-ss}.json";
        var path = Path.Combine(dir, fileName);

        TriggerSerializer.WriteToFile(file, path);
        _log.Information("[FfxivEchoes] バックアップ作成：{Path}", path);

        // 古いバックアップを削除（保持数を超えた分）
        TrimOld(dir);

        return path;
    }

    public IReadOnlyList<BackupInfo> List(string zoneName)
    {
        var dir = Path.Combine(_backupRoot, Sanitize(zoneName));
        if (!Directory.Exists(dir))
        {
            return Array.Empty<BackupInfo>();
        }
        try
        {
            return Directory.EnumerateFiles(dir, "*.json")
                .Select(p =>
                {
                    var info = new FileInfo(p);
                    return new BackupInfo(p, info.LastWriteTime, info.Length);
                })
                .OrderByDescending(b => b.CreatedAtLocal)
                .ToArray();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[FfxivEchoes] バックアップ列挙に失敗：{Zone}", zoneName);
            return Array.Empty<BackupInfo>();
        }
    }

    /// <summary>バックアップファイルから TriggerFile を読み込む（プレビュー用）。</summary>
    public TriggerFile? Load(string backupPath)
    {
        try
        {
            var json = File.ReadAllText(backupPath);
            return JsonSerializer.Deserialize<TriggerFile>(json, new JsonSerializerOptions
            {
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[FfxivEchoes] バックアップ読込に失敗：{Path}", backupPath);
            return null;
        }
    }

    public int CountForZone(string zoneName)
    {
        var dir = Path.Combine(_backupRoot, Sanitize(zoneName));
        if (!Directory.Exists(dir))
        {
            return 0;
        }
        try
        {
            return Directory.EnumerateFiles(dir, "*.json").Count();
        }
        catch
        {
            return 0;
        }
    }

    private void TrimOld(string dir)
    {
        if (_retention <= 0)
        {
            return;
        }
        try
        {
            var files = Directory.EnumerateFiles(dir, "*.json")
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(_retention)
                .ToArray();
            foreach (var f in files)
            {
                try
                {
                    f.Delete();
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[FfxivEchoes] 古いバックアップの削除に失敗：{Path}", f.FullName);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[FfxivEchoes] バックアップディレクトリのトリミングに失敗：{Dir}", dir);
        }
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

public sealed record BackupInfo(string Path, DateTime CreatedAtLocal, long Size);
