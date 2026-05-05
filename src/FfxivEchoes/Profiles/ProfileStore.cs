using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dalamud.Plugin.Services;

namespace FfxivEchoes.Profiles;

/// <summary>
/// {ConfigDirectory}/profiles/*.json を読み書きする。
/// プロファイルが 1 件もない場合、起動時に "default" プロファイルを自動作成する。
/// </summary>
public sealed class ProfileStore
{
    public const string ProfilesDirName = "profiles";
    public const string DefaultProfileName = "default";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _dir;
    private readonly IPluginLog _log;
    private readonly object _gate = new();
    private Dictionary<string, Profile> _profiles = new(StringComparer.OrdinalIgnoreCase);

    public ProfileStore(string configDirectory, IPluginLog log)
    {
        _dir = Path.Combine(configDirectory, ProfilesDirName);
        _log = log;
    }

    public string Directory => _dir;

    public IReadOnlyDictionary<string, Profile> All
    {
        get { lock (_gate) return new Dictionary<string, Profile>(_profiles, StringComparer.OrdinalIgnoreCase); }
    }

    public Profile? Get(string name)
    {
        lock (_gate)
        {
            return _profiles.TryGetValue(name, out var p) ? p : null;
        }
    }

    public void Reload()
    {
        var newDict = new Dictionary<string, Profile>(StringComparer.OrdinalIgnoreCase);
        if (!System.IO.Directory.Exists(_dir))
        {
            try
            {
                System.IO.Directory.CreateDirectory(_dir);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[FfxivEchoes] profiles ディレクトリ作成に失敗");
            }
        }
        else
        {
            foreach (var path in System.IO.Directory.EnumerateFiles(_dir, "*.json").OrderBy(p => p))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    var profile = JsonSerializer.Deserialize<Profile>(json, ReadOptions);
                    if (profile is null || string.IsNullOrWhiteSpace(profile.Name))
                    {
                        continue;
                    }
                    newDict[profile.Name] = profile;
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[FfxivEchoes] プロファイル読込失敗：{Path}", path);
                }
            }
        }

        // default が無ければ作成
        if (!newDict.ContainsKey(DefaultProfileName))
        {
            var def = new Profile
            {
                Name = DefaultProfileName,
                DisplayName = "デフォルト",
            };
            try
            {
                Save(def);
                newDict[def.Name] = def;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[FfxivEchoes] デフォルトプロファイル作成失敗");
            }
        }

        lock (_gate)
        {
            _profiles = newDict;
        }
        _log.Information("[FfxivEchoes] プロファイル読込：{Count} 件", newDict.Count);
    }

    public string Save(Profile profile)
    {
        System.IO.Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, $"{Sanitize(profile.Name)}.json");
        var json = JsonSerializer.Serialize(profile, WriteOptions);
        File.WriteAllText(path, json, new UTF8Encoding(false));
        lock (_gate)
        {
            _profiles[profile.Name] = profile;
        }
        return path;
    }

    public bool Delete(string name)
    {
        if (string.Equals(name, DefaultProfileName, StringComparison.OrdinalIgnoreCase))
        {
            return false; // default は削除不可
        }
        var path = Path.Combine(_dir, $"{Sanitize(name)}.json");
        if (!File.Exists(path))
        {
            return false;
        }
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[FfxivEchoes] プロファイル削除失敗：{Path}", path);
            return false;
        }
        lock (_gate)
        {
            _profiles.Remove(name);
        }
        return true;
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
