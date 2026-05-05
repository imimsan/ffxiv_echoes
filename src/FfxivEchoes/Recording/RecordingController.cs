using Dalamud.Plugin.Services;

namespace FfxivEchoes.Recording;

/// <summary>
/// 録画モードの状態を保持し、戦闘開始時に「このコンテンツを記録するか」を判定する。
/// </summary>
/// <remarks>
/// SPEC.md §9.1.2 に従い、Auto はコンテンツ単位の auto_record 設定に従うが、
/// M4 時点ではトリガー定義ファイルが存在しないため Auto は false 固定。
/// M5/M8 で per-content 設定が読めるようになったら本クラスから参照する。
/// </remarks>
public sealed class RecordingController
{
    private readonly IPluginLog _log;

    public RecordingController(IPluginLog log)
    {
        _log = log;
    }

    public RecordingMode Mode { get; private set; } = RecordingMode.Auto;

    public void SetMode(RecordingMode mode)
    {
        if (Mode == mode)
        {
            return;
        }
        _log.Information("[FfxivEchoes] RecordingMode: {Old} → {New}", Mode, mode);
        Mode = mode;
    }

    /// <summary>戦闘開始時に呼ばれる。指定のゾーンを記録すべきか返す。</summary>
    public bool ShouldRecord(string zoneName)
    {
        return Mode switch
        {
            RecordingMode.ForceOn => true,
            RecordingMode.ForceOff => false,
            // Auto: M4 時点では per-content 設定なしのため false 固定。
            // M5/M8 でトリガー定義の auto_settings.auto_record を参照するように差し替え予定。
            RecordingMode.Auto => false,
            _ => false,
        };
    }
}
