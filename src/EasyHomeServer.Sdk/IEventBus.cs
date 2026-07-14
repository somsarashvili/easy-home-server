namespace EasyHomeServer.Sdk;

/// <summary>
/// In-process, typed publish/subscribe. Lets modules communicate without referencing each
/// other: publisher and subscriber only share the event type, which lives in whichever
/// assembly both can see (the SDK, or a small contracts assembly a module publishes).
/// </summary>
/// <remarks>
/// Delivery is in-memory and best-effort: there is no persistence, no retry and no
/// delivery guarantee across a restart. Handlers are invoked concurrently; a handler that
/// throws is logged and does not affect other subscribers or the publisher.
/// </remarks>
public interface IEventBus
{
    /// <summary>
    /// Publishes an event to all current subscribers of <typeparamref name="TEvent"/> and
    /// completes once every handler has finished. Subscription is by exact type; base types
    /// and interfaces do not receive derived events.
    /// </summary>
    ValueTask PublishAsync<TEvent>(TEvent payload, CancellationToken cancellationToken = default)
        where TEvent : notnull;

    /// <summary>
    /// Subscribes to <typeparamref name="TEvent"/>. Dispose the returned token to
    /// unsubscribe — Blazor components must do so in <c>IDisposable.Dispose</c> or the
    /// component will be kept alive by the bus.
    /// </summary>
    IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler)
        where TEvent : notnull;
}
