using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Actions;

/// <summary>
/// <see cref="TriggerFiredEvent"/> を購読し、アクション群を該当ハンドラへ振り分ける。
/// </summary>
/// <remarks>
/// ActionDefinition.delay が指定されていれば <see cref="Task.Delay"/> で遅延実行。
/// 未知のアクションタイプは警告ログを出して無視する。
/// </remarks>
public sealed class ActionDispatcher : IDisposable
{
    private readonly Dictionary<string, IActionHandler> _handlers;
    private readonly Configuration _configuration;
    private readonly IPluginLog _log;
    private readonly IDisposable _subscription;

    public ActionDispatcher(IEventBus bus, IEnumerable<IActionHandler> handlers, Configuration configuration, IPluginLog log)
    {
        _configuration = configuration;
        _log = log;
        _handlers = new Dictionary<string, IActionHandler>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in handlers)
        {
            _handlers[h.Type] = h;
        }
        _subscription = bus.Subscribe<TriggerFiredEvent>(OnTriggerFired);
    }

    public void Dispose() => _subscription.Dispose();

    private void OnTriggerFired(TriggerFiredEvent ev)
    {
        // 補助：TTS だけのトリガーには自動で overlay_text を追加（auto_visual_for_tts）
        // 何のビジュアルも出ないと「動いてるのか？」が分からないので、デフォルト ON。
        var actions = AugmentActionsForVisibility(ev.Actions);

        foreach (var action in actions)
        {
            if (string.IsNullOrEmpty(action.Type))
            {
                continue;
            }
            if (!_handlers.TryGetValue(action.Type, out var handler))
            {
                _log.Warning("[FfxivEchoes] 未知のアクションタイプ '{Type}'（trigger={Id}）",
                    action.Type, ev.TriggerId);
                continue;
            }

            DispatchOne(handler, action, ev);
        }
    }

    /// <summary>
    /// 視認性のため、TTS のみのアクション集合に overlay_text を補完する。
    /// 既に overlay_text や arena_view が入っていれば何もしない。
    /// Configuration.AutoVisualForTts で OFF にできる。
    /// </summary>
    private List<ActionDefinition> AugmentActionsForVisibility(IReadOnlyList<ActionDefinition> original)
    {
        if (!_configuration.AutoVisualForTts) return new List<ActionDefinition>(original);

        bool hasTts = false;
        bool hasVisual = false;
        string? ttsText = null;
        foreach (var a in original)
        {
            if (string.Equals(a.Type, "tts", StringComparison.OrdinalIgnoreCase))
            {
                hasTts = true;
                ttsText ??= a.Text;
            }
            if (string.Equals(a.Type, "overlay_text", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a.Type, "overlay_corner_text", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a.Type, "arena_view", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a.Type, "timer_bar", StringComparison.OrdinalIgnoreCase))
            {
                hasVisual = true;
            }
        }
        var list = new List<ActionDefinition>(original);
        if (hasTts && !hasVisual && !string.IsNullOrEmpty(ttsText))
        {
            list.Add(new ActionDefinition
            {
                Type = "overlay_text",
                Text = ttsText,
                Duration = 3.0,
                Color = "#FBBF24",
                Size = "large",
            });
        }
        return list;
    }

    private void DispatchOne(IActionHandler handler, ActionDefinition action, TriggerFiredEvent ev)
    {
        if (action.Delay <= 0)
        {
            ExecuteSafe(handler, action, ev);
            return;
        }

        // 遅延実行：fire-and-forget。例外はログのみ。
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(action.Delay)).ConfigureAwait(false);
                ExecuteSafe(handler, action, ev);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[FfxivEchoes] 遅延実行中の例外（trigger={Id}, type={Type}）",
                    ev.TriggerId, action.Type);
            }
        });
    }

    private void ExecuteSafe(IActionHandler handler, ActionDefinition action, TriggerFiredEvent ev)
    {
        try
        {
            handler.Execute(action, ev);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[FfxivEchoes] アクション実行例外（trigger={Id}, type={Type}）",
                ev.TriggerId, action.Type);
        }
    }
}
