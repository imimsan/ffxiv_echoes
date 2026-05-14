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

    /// <summary>true: トリガー発火時にチャットへ「[FFXIV Echoes] Trigger 発火: ...」を出す。
    /// 音声 / オーバーレイの動作確認や、設定が機能しているか目視で確認するためのフォールバック。
    /// デフォルト ON。</summary>
    public bool EchoTriggerFires { get; set; } = true;

    /// <summary>true: TTS のみのトリガーが発火したとき、自動で overlay_text を追加して
    /// 画面中央にも 3 秒間その文字を出す。デフォルト OFF（中央表示は邪魔なため）。
    /// 必要なときはユーザーが各トリガーに明示的に overlay_text を入れる。</summary>
    public bool AutoVisualForTts { get; set; } = false;


    // プロファイル
    public string ActiveProfile { get; set; } = "default";

    // 音声（SPEC.md §10.2 三階層音量制御の上位 2 階層）
    public string DefaultVoice { get; set; } = "ja-JP-Default";
    public float MasterVolume { get; set; } = 1.0f;
    public float TtsVolume { get; set; } = 1.0f;
    public float WavVolume { get; set; } = 1.0f;
    public float FeedbackVolume { get; set; } = 0.8f;
    public string? AudioDevice { get; set; } = null;

    /// <summary>
    /// TTS のブースト倍率（NAudio 経由で SAPI 出力を増幅）。
    /// 1.0 = SAPI そのまま、2.0 = 約 +6dB、3.0 = 約 +9.5dB、5.0 = 約 +14dB。最大 5.0。
    /// SAPI 単体だと最大 100% で頭打ちなので、ここを上げると更に大音量になる。
    /// 3.0 を超えると元音源によってはクリッピング（音割れ）が発生する場合あり。
    /// </summary>
    public float TtsBoost { get; set; } = 2.0f;

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
        TtsBoost = Math.Clamp(TtsBoost, 1.0f, 5.0f);
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
