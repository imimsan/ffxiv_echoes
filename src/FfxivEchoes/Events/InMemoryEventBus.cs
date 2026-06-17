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
    // 購読リストはコピーオンライト（不変配列を差し替え）にする。Publish は lock 内で配列の参照だけを
    // 受け取り、列挙は lock 外で行う。これにより Publish ごとの ToArray アロケーションを無くし、
    // raid-wide フレームで HpChanged/StatusGained が数十件同時 publish されても GC churn を生まない。
    private readonly Dictionary<Type, Delegate[]> _handlersByType = new();
    private Action<IGameEvent>[] _allHandlers = Array.Empty<Action<IGameEvent>>();
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
            // 参照取得のみ（配列は不変なので lock 外で安全に列挙できる）。アロケーション無し。
            typed = _handlersByType.TryGetValue(typeof(TEvent), out var arr)
                ? arr
                : Array.Empty<Delegate>();
            global = _allHandlers;
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
            _handlersByType.TryGetValue(typeof(TEvent), out var arr);
            _handlersByType[typeof(TEvent)] = AppendCopy(arr, handler);
        }

        return new Subscription(() =>
        {
            lock (_gate)
            {
                if (_handlersByType.TryGetValue(typeof(TEvent), out var arr))
                {
                    _handlersByType[typeof(TEvent)] = RemoveCopy(arr, handler);
                }
            }
        });
    }

    public IDisposable SubscribeAll(Action<IGameEvent> handler)
    {
        lock (_gate)
        {
            _allHandlers = AppendCopy(_allHandlers, handler);
        }
        return new Subscription(() =>
        {
            lock (_gate)
            {
                _allHandlers = RemoveCopy(_allHandlers, handler);
            }
        });
    }

    // コピーオンライト用ヘルパ（呼び出し側は _gate を保持していること）。
    private static T[] AppendCopy<T>(T[]? source, T item) where T : Delegate
    {
        if (source is null || source.Length == 0)
        {
            return new[] { item };
        }
        var next = new T[source.Length + 1];
        Array.Copy(source, next, source.Length);
        next[source.Length] = item;
        return next;
    }

    private static T[] RemoveCopy<T>(T[] source, T item) where T : Delegate
    {
        var idx = Array.IndexOf(source, item);
        if (idx < 0)
        {
            return source;
        }
        if (source.Length == 1)
        {
            return Array.Empty<T>();
        }
        var next = new T[source.Length - 1];
        Array.Copy(source, 0, next, 0, idx);
        Array.Copy(source, idx + 1, next, idx, source.Length - idx - 1);
        return next;
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
