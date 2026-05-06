using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin.Services;

namespace FfxivEchoes.Triggers;

/// <summary>
/// cast_id / 名前 → gimmick の対応を保持する学習辞書。
/// </summary>
/// <remarks>
/// pluginConfigs/FfxivEchoes/safe_call_dictionary.json から読み込む。
/// MultiCastDetector が検出した結果を「学習結果」としてここに書き戻す。
/// 手動編集も可能で、ユーザー間で共有できる JSON フォーマット。
/// </remarks>
public sealed class SafeCallDictionary
{
    private const string FileName = "safe_call_dictionary.json";

    private readonly string _path;
    private readonly IPluginLog _log;
    private readonly object _gate = new();

    private DictionaryFile _data = new();

    public SafeCallDictionary(string configDirectory, IPluginLog log)
    {
        _path = Path.Combine(configDirectory, FileName);
        _log = log;
        Reload();
    }

    public void Reload()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    _data = new DictionaryFile();
                    return;
                }
                var json = File.ReadAllText(_path);
                var loaded = JsonSerializer.Deserialize<DictionaryFile>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                _data = loaded ?? new DictionaryFile();
                _log.Information("[FfxivEchoes] SafeCallDictionary loaded: {Overrides} overrides, {Patterns} patterns",
                    _data.Overrides.Count, _data.NamePatterns.Count);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[FfxivEchoes] SafeCallDictionary 読み込み失敗、空で初期化");
                _data = new DictionaryFile();
            }
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
                File.WriteAllText(_path, json);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[FfxivEchoes] SafeCallDictionary 保存失敗");
            }
        }
    }

    /// <summary>cast_id（"0x189C" 形式）または cast_name から override を引く。</summary>
    public DictionaryEntry? Lookup(uint actionId, string? castName)
    {
        lock (_gate)
        {
            // 1. cast_id 完全一致（最優先）
            var idKey = $"0x{actionId:X4}";
            if (_data.Overrides.TryGetValue(idKey, out var entry))
            {
                return entry;
            }

            // 2. 名前パターン
            if (!string.IsNullOrEmpty(castName))
            {
                foreach (var pat in _data.NamePatterns)
                {
                    if (string.IsNullOrEmpty(pat.Match)) continue;
                    if (castName.Contains(pat.Match, StringComparison.OrdinalIgnoreCase))
                    {
                        return new DictionaryEntry
                        {
                            Gimmick = pat.Gimmick,
                            Callout = pat.Callout,
                            Tts = pat.Tts,
                            FanDeg = pat.FanDeg,
                            Source = pat.Source ?? "name_pattern",
                        };
                    }
                }
            }
        }
        return null;
    }

    /// <summary>MultiCastDetector 等から学習結果を追記。</summary>
    public void Upsert(string castIdOrPattern, DictionaryEntry entry)
    {
        if (string.IsNullOrEmpty(castIdOrPattern)) return;
        lock (_gate)
        {
            _data.Overrides[castIdOrPattern] = entry;
        }
        Save();
    }

    /// <summary>すべての override を取得（UI 表示用）。</summary>
    public IReadOnlyDictionary<string, DictionaryEntry> AllOverrides()
    {
        lock (_gate) return new Dictionary<string, DictionaryEntry>(_data.Overrides);
    }

    public IReadOnlyList<NamePattern> AllPatterns()
    {
        lock (_gate) return _data.NamePatterns.ToArray();
    }

    private sealed class DictionaryFile
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.0";

        [JsonPropertyName("overrides")]
        public Dictionary<string, DictionaryEntry> Overrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("name_patterns")]
        public List<NamePattern> NamePatterns { get; set; } = new();
    }

    public sealed class DictionaryEntry
    {
        [JsonPropertyName("gimmick")]
        public string Gimmick { get; set; } = "inner_circle";

        [JsonPropertyName("callout")]
        public string? Callout { get; set; }

        [JsonPropertyName("tts")]
        public string? Tts { get; set; }

        [JsonPropertyName("fan_deg")]
        public double? FanDeg { get; set; }

        /// <summary>"manual" / "name_pattern" / "multi_cast_detected" / "omen" のどれか</summary>
        [JsonPropertyName("source")]
        public string? Source { get; set; }

        [JsonPropertyName("confidence")]
        public double? Confidence { get; set; }
    }

    public sealed class NamePattern
    {
        [JsonPropertyName("match")]
        public string Match { get; set; } = string.Empty;

        [JsonPropertyName("gimmick")]
        public string Gimmick { get; set; } = "inner_circle";

        [JsonPropertyName("callout")]
        public string? Callout { get; set; }

        [JsonPropertyName("tts")]
        public string? Tts { get; set; }

        [JsonPropertyName("fan_deg")]
        public double? FanDeg { get; set; }

        [JsonPropertyName("source")]
        public string? Source { get; set; }
    }
}
