using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FfxivEchoes.Events;
using FfxivEchoes.Triggers;
using FfxivEchoes.Triggers.Models;

namespace FfxivEchoes.Actions.Handlers;

/// <summary>
/// 別トリガーを連鎖発動（SPEC.md §5.1 chain_trigger）。
/// </summary>
/// <remarks>
/// action.trigger_id で指定された ID のトリガーを現在ゾーンの TriggerFile から検索し、
/// そのトリガーの actions を直接実行する形で TriggerFiredEvent を発行する。
/// マッチ条件の評価は行わない（強制発動）。
/// 無限ループ防止のため、1 回の連鎖チェーン内では同じ trigger_id を 2 回以上呼ばない。
/// </remarks>
public sealed class ChainTriggerHandler : IActionHandler
{
    public string Type => "chain_trigger";

    private readonly IEventBus _bus;
    private readonly TriggerStore _store;
    private readonly IPluginLog _log;

    [ThreadStatic]
    private static HashSet<string>? _currentChain;

    public ChainTriggerHandler(IEventBus bus, TriggerStore store, IPluginLog log)
    {
        _bus = bus;
        _store = store;
        _log = log;
    }

    public void Execute(ActionDefinition action, TriggerFiredEvent context)
    {
        if (string.IsNullOrEmpty(action.TriggerId))
        {
            _log.Warning("[FfxivEchoes] chain_trigger に trigger_id 指定がありません");
            return;
        }

        _currentChain ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!_currentChain.Add(action.TriggerId))
        {
            _log.Warning("[FfxivEchoes] chain_trigger ループ検出：{Id}（無視）", action.TriggerId);
            return;
        }

        try
        {
            var triggerFile = _store.GetByZone(context.Zone);
            if (triggerFile is null)
            {
                _log.Warning("[FfxivEchoes] chain_trigger：ゾーン '{Zone}' のトリガーファイル未ロード", context.Zone);
                return;
            }

            TriggerDefinition? target = null;
            foreach (var t in triggerFile.Triggers)
            {
                if (string.Equals(t.Id, action.TriggerId, StringComparison.OrdinalIgnoreCase))
                {
                    target = t;
                    break;
                }
            }
            if (target is null)
            {
                _log.Warning("[FfxivEchoes] chain_trigger：ID '{Id}' のトリガーが見つかりません", action.TriggerId);
                return;
            }

            _bus.Publish(new TriggerFiredEvent(
                Timestamp: DateTimeOffset.UtcNow,
                Zone: context.Zone,
                TriggerId: target.Id,
                TriggerName: target.Name,
                Actions: target.Actions,
                SourceEvent: context.SourceEvent));
        }
        finally
        {
            _currentChain.Remove(action.TriggerId);
            if (_currentChain.Count == 0)
            {
                _currentChain = null;
            }
        }
    }
}
