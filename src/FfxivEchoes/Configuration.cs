using System;
using Dalamud.Configuration;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace FfxivEchoes;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    // 全体（SPEC.md §13.1）
    public string Language { get; set; } = "ja";
    public bool DebugMode { get; set; } = false;

    // プロファイル
    public string ActiveProfile { get; set; } = "default";

    // 音声（SPEC.md §10.2 三階層音量制御の上位 2 階層）
    public string DefaultVoice { get; set; } = "ja-JP-Default";
    public float MasterVolume { get; set; } = 0.8f;
    public float TtsVolume { get; set; } = 1.0f;
    public float WavVolume { get; set; } = 1.0f;
    public float FeedbackVolume { get; set; } = 0.8f;
    public string? AudioDevice { get; set; } = null;

    // 取り込み・ログ保管
    public double MergeToleranceSeconds { get; set; } = 2.0;
    public int? LogRetentionDays { get; set; } = null;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }

    public void ClampToValidRanges()
    {
        MasterVolume = Math.Clamp(MasterVolume, 0f, 1f);
        TtsVolume = Math.Clamp(TtsVolume, 0f, 1f);
        WavVolume = Math.Clamp(WavVolume, 0f, 1f);
        FeedbackVolume = Math.Clamp(FeedbackVolume, 0f, 1f);
        MergeToleranceSeconds = Math.Clamp(MergeToleranceSeconds, 0.0, 60.0);
        if (LogRetentionDays is { } days && days <= 0)
        {
            LogRetentionDays = null;
        }
    }

    public static Configuration LoadAndMigrate(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        var loaded = pluginInterface.GetPluginConfig() as Configuration;

        if (loaded is null)
        {
            log.Information("[FfxivEchoes] 設定ファイルが見つからないため新規作成します");
            var fresh = new Configuration();
            pluginInterface.SavePluginConfig(fresh);
            return fresh;
        }

        if (loaded.Version > CurrentVersion)
        {
            log.Warning(
                "[FfxivEchoes] 設定ファイルのバージョンがプラグインより新しい (config v{Loaded} > plugin v{Current})。" +
                "デフォルト設定でフォールバックします（既存設定は上書きしません）。",
                loaded.Version, CurrentVersion);
            return new Configuration { Version = loaded.Version };
        }

        if (loaded.Version < CurrentVersion)
        {
            log.Information(
                "[FfxivEchoes] 設定ファイルを v{From} → v{To} に移行します",
                loaded.Version, CurrentVersion);
            // 将来：loaded.Version 別の移行処理をここに追加
            loaded.Version = CurrentVersion;
            pluginInterface.SavePluginConfig(loaded);
        }

        loaded.ClampToValidRanges();
        return loaded;
    }
}
