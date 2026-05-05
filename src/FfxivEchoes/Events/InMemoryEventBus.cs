using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;

namespace FfxivEchoes.Events;

/// <summary>
/// シンプルな同期 in-memory イベントバス。
/// </summary>
/// <remarks>
/// プラグインのイベント発行はメインスレッド（IFramework.Update / TerritoryChanged 等）から
/// 行われるため、購読者の呼び出しもメインスレッドで同期実行する。長時間処理を行う購読者は
/// 自分でスレッドプール等にディスパッチすること。
/// </remarks>
public sealed class InMemoryEventBus : IEventBus
{
    private readonly Dictionary<Type, List<Delegate>> _handlersByType = new();
    private readonly List<Action<IGameEvent>> _allHandlers = new();
    private readonly object _gate = new();
    private readonly IPluginLog _log;

    public InMemoryEventBus(IPluginLog log)
    {
        _log = log;
    }

    public void Publish<TEvent>(TEvent ev) where TEvent : IGameEvent
    {
        Delegate[] typed;
        Action<IGameEvent>[] global;
        lock (_gate)
        {
            typed = _handlersByType.TryGetValue(typeof(TEvent), out var list)
                ? list.ToArray()
                : Array.Empty<Delegate>();
            global = _allHandlers.ToArray();
        }

        foreach (var d in typed)
        {
            try { ((Action<TEvent>)d)(ev); }
            catch (Exception ex) { _log.Error(ex, "[FfxivEchoes] Event subscriber threw on {Type}", typeof(TEvent).Name); }
        }

        foreach (var h in global)
        {
            try { h(ev); }
            catch (Exception ex) { _log.Error(ex, "[FfxivEchoes] Global event subscriber threw on {Type}", typeof(TEvent).Name); }
        }
    }

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : IGameEvent
    {
        lock (_gate)
        {
            if (!_handlersByType.TryGetValue(typeof(TEvent), out var list))
            {
                list = new List<Delegate>();
                _handlersByType[typeof(TEvent)] = list;
            }
            list.Add(handler);
        }

        return new Subscription(() =>
        {
            lock (_gate)
            {
                if (_handlersByType.TryGetValue(typeof(TEvent), out var list))
                {
                    list.Remove(handler);
                }
            }
        });
    }

    public IDisposable SubscribeAll(Action<IGameEvent> handler)
    {
        lock (_gate)
        {
            _allHandlers.Add(handler);
        }
        return new Subscription(() =>
        {
            lock (_gate)
            {
                _allHandlers.Remove(handler);
            }
        });
    }

    private sealed class Subscription : IDisposable
    {
        private Action? _onDispose;
        public Subscription(Action onDispose) => _onDispose = onDispose;
        public void Dispose()
        {
            var d = System.Threading.Interlocked.Exchange(ref _onDispose, null);
            d?.Invoke();
        }
    }
}
