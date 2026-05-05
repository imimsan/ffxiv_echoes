using Dalamud.Plugin.Services;
using FfxivEchoes.Triggers;

namespace FfxivEchoes.Recording;

/// <summary>
/// 録画モードの状態を保持し、戦闘開始時に「このコンテンツを記録するか」を判定する。
/// </summary>
/// <remarks>
/// SPEC.md §9.1.2 に従う：
/// - ForceOn / ForceOff：手動オーバーライド
/// - Auto：トリガー定義の <c>auto_settings.auto_record</c> を参照（M5 で実装）
/// </remarks>
public sealed class RecordingController
{
    private readonly IPluginLog _log;
    private readonly TriggerStore _triggerStore;

    public RecordingController(IPluginLog log, TriggerStore triggerStore)
    {
        _log = log;
        _triggerStore = triggerStore;
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
            RecordingMode.Auto => _triggerStore.GetByZone(zoneName)?.AutoSettings.AutoRecord ?? false,
            _ => false,
        };
    }
}
