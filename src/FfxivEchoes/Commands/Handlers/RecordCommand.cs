using Dalamud.Plugin.Services;
using FfxivEchoes.Recording;

namespace FfxivEchoes.Commands.Handlers;

/// <summary>
/// <c>/echoes record &lt;on|off|auto&gt;</c>。SPEC.md §9.1.2 に従う一時オーバーライド。
/// </summary>
public sealed class RecordCommand : ICommandHandler
{
    public string Verb => "record";
    public string Usage => "record <on|off|auto>";
    public string Description => "戦闘ログ記録モードの切替（on/off/auto）";

    private readonly RecordingController _controller;
    private readonly BattleRecorder _recorder;
    private readonly IChatGui _chatGui;

    public RecordCommand(RecordingController controller, BattleRecorder recorder, IChatGui chatGui)
    {
        _controller = controller;
        _recorder = recorder;
        _chatGui = chatGui;
    }

    public void Execute(string args)
    {
        var token = args.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(token) || token == "status")
        {
            ReportStatus();
            return;
        }

        var mode = token switch
        {
            "on" or "force-on" or "true" or "1" => (RecordingMode?)RecordingMode.ForceOn,
            "off" or "force-off" or "false" or "0" => RecordingMode.ForceOff,
            "auto" => RecordingMode.Auto,
            _ => null,
        };

        if (mode is null)
        {
            _chatGui.Print($"[FFXIV Echoes] 使い方: {CommandRouter.RootCommand} {Usage}");
            return;
        }

        _controller.SetMode(mode.Value);
        _chatGui.Print($"[FFXIV Echoes] 録画モード: {ModeLabel(mode.Value)}");
        if (mode.Value == RecordingMode.Auto)
        {
            _chatGui.Print("  Auto は現在ゾーンの auto_settings.auto_record に従います。");
        }
    }

    private void ReportStatus()
    {
        _chatGui.Print(
            $"[FFXIV Echoes] 録画モード: {ModeLabel(_controller.Mode)}, " +
            $"記録中: {(_recorder.IsRecording ? "YES" : "no")}");
        if (_recorder.IsRecording && _recorder.CurrentFilePath is { } path)
        {
            _chatGui.Print($"  → {path} ({_recorder.CurrentEventCount} events)");
        }
    }

    private static string ModeLabel(RecordingMode mode) => mode switch
    {
        RecordingMode.ForceOn => "ON（強制）",
        RecordingMode.ForceOff => "OFF（強制）",
        RecordingMode.Auto => "AUTO（コンテンツ別設定に従う）",
        _ => mode.ToString(),
    };
}
