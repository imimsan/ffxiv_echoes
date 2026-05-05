using FfxivEchoes.Events;
using FfxivEchoes.Triggers.Models;
using FfxivEchoes.Windows;

namespace FfxivEchoes.Actions.Handlers;

/// <summary>
/// 俯瞰アリーナ図を表示するアクション（arena_view）。
/// </summary>
/// <remarks>
/// gimmick タイプ（outer_ring / inner_circle / scatter / stack / cone）と callout 文字列を受け取り、
/// MinimapWindow に転送する。デモ（demo/demo.js の renderArena）と同じ視覚的概念を実機に持ち込む。
/// </remarks>
public sealed class ArenaViewHandler : IActionHandler
{
    public string Type => "arena_view";

    private readonly MinimapWindow _minimap;

    public ArenaViewHandler(MinimapWindow minimap)
    {
        _minimap = minimap;
    }

    public void Execute(ActionDefinition action, TriggerFiredEvent context)
    {
        if (string.IsNullOrEmpty(action.Gimmick))
        {
            return;
        }
        _minimap.AddArenaView(
            gimmick: action.Gimmick,
            callout: action.Callout,
            durationSec: action.Duration ?? 5.0,
            direction: action.Direction,
            fanDeg: action.FanDeg,
            arenaRadius: action.ArenaRadius);
    }
}
