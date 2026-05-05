using System;

namespace FfxivEchoes.Events;

/// <summary>
/// プラグイン内部のイベントバス。Capture 層から発行され、Recording / Trigger / Diagnostics が購読する。
/// </summary>
public interface IEventBus
{
    /// <summary>イベントを発行する。型 <typeparamref name="TEvent"/> 専用購読者と SubscribeAll の両方に配信する。</summary>
    void Publish<TEvent>(TEvent ev) where TEvent : IGameEvent;

    /// <summary>特定型のイベントを購読する。返値を Dispose で解除。</summary>
    IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : IGameEvent;

    /// <summary>全イベントを購読する（診断用途）。返値を Dispose で解除。</summary>
    IDisposable SubscribeAll(Action<IGameEvent> handler);
}
