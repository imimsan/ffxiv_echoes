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
    private readonly IPluginLog _log;
    private readonly IDisposable _subscription;

    public ActionDispatcher(IEventBus bus, IEnumerable<IActionHandler> handlers, IPluginLog log)
    {
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
        foreach (var action in ev.Actions)
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
