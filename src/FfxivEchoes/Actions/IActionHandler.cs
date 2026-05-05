using FfxivEchoes.Events;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Actions;

/// <summary>
/// 単一のアクションタイプ（tts / wav / overlay_text 等）を実行するハンドラ。
/// </summary>
public interface IActionHandler
{
    /// <summary>担当するアクションタイプ（ActionDefinition.Type と一致）。</summary>
    string Type { get; }

    /// <summary>アクションを実行する。delay は ActionDispatcher 側で適用済み。</summary>
    void Execute(ActionDefinition action, TriggerFiredEvent context);
}
