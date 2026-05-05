using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin.Services;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Triggers;

/// <summary>
/// <c>triggers/</c> ディレクトリ配下の <c>*.json</c> をスキャンしてパースする。
/// </summary>
/// <remarks>
/// SPEC.md §2.3 のファイル配置に従い、ConfigDirectory/triggers/ をルートとする。
/// パースに失敗したファイルは <see cref="TriggerLoadResult.Error"/> で報告し、
/// 他のファイルの読み込みは続行する（1 ファイルの破損で全体が止まらないように）。
/// </remarks>
public sealed class TriggerLoader
{
    public const string TriggersDirName = "triggers";
    public const string SupportedSchemaVersion = "1.0";

    private readonly string _triggersDir;
    private readonly IPluginLog _log;
    private readonly JsonSerializerOptions _jsonOptions;

    public TriggerLoader(string configDirectory, IPluginLog log)
    {
        _triggersDir = Path.Combine(configDirectory, TriggersDirName);
        _log = log;
        _jsonOptions = new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
    }

    public string TriggersDirectory => _triggersDir;

    public IReadOnlyList<TriggerLoadResult> LoadAll()
    {
        var results = new List<TriggerLoadResult>();
        if (!Directory.Exists(_triggersDir))
        {
            _log.Information("[FfxivEchoes] triggers ディレクトリが存在しないため作成：{Path}", _triggersDir);
            try
            {
                Directory.CreateDirectory(_triggersDir);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[FfxivEchoes] triggers ディレクトリの作成に失敗");
                return results;
            }
            return results;
        }

        var files = Directory
            .EnumerateFiles(_triggersDir, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var path in files)
        {
            results.Add(LoadFile(path));
        }

        return results;
    }

    public TriggerLoadResult LoadFile(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize<TriggerFile>(json, _jsonOptions);
            if (file is null)
            {
                return TriggerLoadResult.Failure(path, "JSON のデシリアライズが null を返しました（空ファイル？）");
            }

            var warnings = new List<string>();
            ValidateBasic(file, warnings);

            return TriggerLoadResult.Success(path, file, warnings);
        }
        catch (JsonException ex)
        {
            return TriggerLoadResult.Failure(path, $"JSON パースエラー: {ex.Message}");
        }
        catch (IOException ex)
        {
            return TriggerLoadResult.Failure(path, $"ファイル読込エラー: {ex.Message}");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[FfxivEchoes] トリガーファイル読み込みで予期しない例外：{Path}", path);
            return TriggerLoadResult.Failure(path, $"想定外のエラー: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>軽量な構造検証（スキーマ準拠の最低限のチェック）。失敗時は warnings に追加。</summary>
    private static void ValidateBasic(TriggerFile file, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(file.Zone))
        {
            warnings.Add("zone フィールドが未設定です");
        }
        if (string.IsNullOrWhiteSpace(file.Version))
        {
            warnings.Add("version フィールドが未設定です");
        }
        else if (file.Version != SupportedSchemaVersion)
        {
            warnings.Add($"version が {SupportedSchemaVersion} 以外（実際: {file.Version}）。読み込みは継続しますが互換性に注意");
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var trigger in file.Triggers)
        {
            if (string.IsNullOrWhiteSpace(trigger.Id))
            {
                warnings.Add("ID 未設定のトリガーが存在します");
                continue;
            }
            if (!seenIds.Add(trigger.Id))
            {
                warnings.Add($"トリガー ID が重複: {trigger.Id}");
            }
            if (string.IsNullOrWhiteSpace(trigger.Type))
            {
                warnings.Add($"トリガー '{trigger.Id}' の type が未設定");
            }
            if (trigger.Actions.Count == 0 && trigger.SetVariable is null)
            {
                warnings.Add($"トリガー '{trigger.Id}' に actions も set_variable もありません（何も発動しません）");
            }
        }
    }
}
