using System;
using System.Collections.Generic;
using System.IO;
using Dalamud.Plugin.Services;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Triggers;

/// <summary>
/// メモリ上のトリガー定義キャッシュ。zone 名でクエリできる。
/// <see cref="Reload"/> でファイルシステムから再読込する。
/// </summary>
public sealed class TriggerStore
{
    private readonly TriggerLoader _loader;
    private readonly TriggerBackupManager? _backupManager;
    private readonly IPluginLog _log;
    private readonly object _gate = new();

    private Dictionary<string, TriggerFile> _byZone = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _zoneToFilePath = new(StringComparer.OrdinalIgnoreCase);
    private List<TriggerLoadResult> _lastResults = new();

    public TriggerStore(TriggerLoader loader, IPluginLog log, TriggerBackupManager? backupManager = null)
    {
        _loader = loader;
        _backupManager = backupManager;
        _log = log;
    }

    public TriggerBackupManager? BackupManager => _backupManager;

    /// <summary>最後の <see cref="Reload"/> 結果が完了した時刻。</summary>
    public DateTimeOffset? LastReloadedAt { get; private set; }

    /// <summary>
    /// <see cref="Reload"/> が完了するたびに増える単調カウンタ。
    /// UI が外部保存・削除を検知して作業コピーを再同期するために使う。
    /// </summary>
    public long ReloadVersion { get; private set; }

    /// <summary>現在ロード済みのファイル数（成功のみ）。</summary>
    public int LoadedFileCount
    {
        get { lock (_gate) { return _byZone.Count; } }
    }

    /// <summary>最後のロード時のエラー件数。</summary>
    public int LastErrorCount
    {
        get
        {
            lock (_gate)
            {
                var n = 0;
                foreach (var r in _lastResults)
                {
                    if (!r.IsSuccess)
                    {
                        n++;
                    }
                }
                return n;
            }
        }
    }

    public event Action? Reloaded;

    public TriggerFile? GetByZone(string zone)
    {
        lock (_gate)
        {
            return _byZone.TryGetValue(zone, out var file) ? file : null;
        }
    }

    public IReadOnlyDictionary<string, TriggerFile> Snapshot()
    {
        lock (_gate)
        {
            return new Dictionary<string, TriggerFile>(_byZone, StringComparer.OrdinalIgnoreCase);
        }
    }

    public IReadOnlyList<TriggerLoadResult> LastResults
    {
        get
        {
            lock (_gate)
            {
                return _lastResults.AsReadOnly();
            }
        }
    }

    /// <summary>ファイルシステムから再読込する。</summary>
    public void Reload()
    {
        var results = _loader.LoadAll();

        var newByZone = new Dictionary<string, TriggerFile>(StringComparer.OrdinalIgnoreCase);
        var newZoneToFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dupes = new List<string>();

        foreach (var r in results)
        {
            if (!r.IsSuccess)
            {
                _log.Warning("[FfxivEchoes] トリガー読み込み失敗：{Path}：{Error}",
                    r.FilePath, r.Error ?? "(詳細なし)");
                continue;
            }
            foreach (var w in r.Warnings)
            {
                _log.Warning("[FfxivEchoes] {Path}：{Warning}", Path.GetFileName(r.FilePath), w);
            }

            var zone = r.File!.Zone;
            if (string.IsNullOrWhiteSpace(zone))
            {
                _log.Warning("[FfxivEchoes] zone 未設定のトリガーファイルをスキップ：{Path}", r.FilePath);
                continue;
            }

            if (newByZone.TryGetValue(zone, out var existing))
            {
                var existingPath = newZoneToFile.TryGetValue(zone, out var p) ? p : "?";
                _log.Warning("[FfxivEchoes] 同一 zone のファイルが複数存在：{Zone} ({Existing} と {New})。後者を採用します",
                    zone, existingPath, r.FilePath);
                dupes.Add(zone);
            }

            // マイグレーション：旧版 StrategyDraftGenerator が自動生成していた
            // 「何も表示/通知しない object_appear / hp_change ノイズ」だけを無効化。
            // AoE/マーカー/通知など意味のある手動 mechanic は reload のたびに潰してはいけない。
            var disabledAutoNoise = 0;
            foreach (var profile in r.File.StrategyProfiles)
            {
                foreach (var mech in profile.Mechanics)
                {
                    if (!mech.Enabled) continue;
                    if (IsLegacyAutoNoiseMechanic(mech))
                    {
                        mech.Enabled = false;
                        disabledAutoNoise++;
                    }
                }
            }
            if (disabledAutoNoise > 0)
            {
                _log.Information(
                    "[FfxivEchoes] {Zone}: object_appear/hp_change 由来の旧自動生成 mechanic を {N} 件無効化（タイムライン汚染防止）",
                    zone, disabledAutoNoise);
            }

            newByZone[zone] = r.File;
            newZoneToFile[zone] = r.FilePath;
        }

        lock (_gate)
        {
            _byZone = newByZone;
            _zoneToFilePath = newZoneToFile;
            _lastResults = new List<TriggerLoadResult>(results);
            LastReloadedAt = DateTimeOffset.UtcNow;
            ReloadVersion++;
        }

        var loaded = newByZone.Count;
        var errors = 0;
        foreach (var r in results)
        {
            if (!r.IsSuccess)
            {
                errors++;
            }
        }
        _log.Information("[FfxivEchoes] トリガー再読込：成功 {Loaded} 件、エラー {Errors} 件 (zones: {Zones})",
            loaded, errors, string.Join(", ", newByZone.Keys));

        try
        {
            Reloaded?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[FfxivEchoes] Reloaded ハンドラで例外");
        }
    }

    public string? GetFilePathForZone(string zone)
    {
        lock (_gate)
        {
            return _zoneToFilePath.TryGetValue(zone, out var path) ? path : null;
        }
    }

    /// <summary>
    /// 指定ゾーンの TriggerFile を JSON ファイルに書き出す。新規ゾーンの場合は
    /// <c>{TriggersDir}/{zone}.json</c> を生成。書き出し後に <see cref="Reload"/> を呼ぶ。
    /// </summary>
    public string SaveZone(string zone, TriggerFile file)
    {
        // 既存ファイルがあればバックアップを取ってから上書き
        var existingPath = GetFilePathForZone(zone);
        if (existingPath is not null && _backupManager is not null)
        {
            var existing = GetByZone(zone);
            if (existing is not null)
            {
                try
                {
                    _backupManager.CreateBackup(zone, existing);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[FfxivEchoes] バックアップ作成に失敗（保存は続行）：{Zone}", zone);
                }
            }
        }

        var path = existingPath ?? Path.Combine(_loader.TriggersDirectory, $"{Sanitize(zone)}.json");
        TriggerSerializer.WriteToFile(file, path);
        _log.Information("[FfxivEchoes] トリガー定義を保存：{Path}", path);
        Reload();
        return path;
    }

    /// <summary>
    /// 指定ゾーンの TriggerFile（JSON）を物理削除する。録画ファイルや backup は触らない。
    /// バックアップは事前に取得する。リロードまで実行。
    /// </summary>
    public bool DeleteZone(string zone)
    {
        var path = GetFilePathForZone(zone);
        if (path is null)
        {
            return false;
        }
        // 削除前に最終バックアップを残しておく
        if (_backupManager is not null)
        {
            var existing = GetByZone(zone);
            if (existing is not null)
            {
                try
                {
                    _backupManager.CreateBackup(zone, existing);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[FfxivEchoes] 削除前バックアップに失敗（削除は続行）：{Zone}", zone);
                }
            }
        }

        try
        {
            File.Delete(path);
            _log.Information("[FfxivEchoes] トリガー定義を削除：{Path}", path);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[FfxivEchoes] トリガー定義の削除に失敗：{Path}", path);
            throw;
        }
        Reload();
        return true;
    }

    /// <summary>バックアップファイルから現在の定義を復元する（バックアップ取得 → 上書き → リロード）。</summary>
    public bool RestoreFromBackup(string zone, string backupPath)
    {
        if (_backupManager is null)
        {
            _log.Warning("[FfxivEchoes] BackupManager 未構成のため復元不可");
            return false;
        }
        var loaded = _backupManager.Load(backupPath);
        if (loaded is null)
        {
            return false;
        }
        SaveZone(zone, loaded);
        _log.Information("[FfxivEchoes] バックアップから復元：{Zone} ← {Path}", zone, backupPath);
        return true;
    }

    public static bool IsLegacyAutoNoiseMechanic(MechanicStrategy mech)
    {
        var src = mech.SourceEventType;
        if (!string.Equals(src, "object_appear", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(src, "hp_change", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(mech.Callout) &&
               string.IsNullOrWhiteSpace(mech.WarningText) &&
               string.IsNullOrWhiteSpace(mech.Gimmick) &&
               mech.SafeZone is null &&
               mech.Triggers.Count == 0 &&
               mech.SpreadPositions.Count == 0 &&
               mech.ObjectMarkers.Count == 0 &&
               mech.AoeZones.Count == 0 &&
               mech.AoeSequence is null &&
               mech.PartyStatusHighlights.Count == 0;
    }

    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "Unknown";
        }
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }
        return sb.ToString();
    }
}
