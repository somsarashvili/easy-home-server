using System.Collections.Concurrent;
using EasyHomeServer.Sdk;

namespace EasyHomeServer.Host.Infrastructure;

/// <summary>
/// In-process implementation of <see cref="IEventBus"/>: a per-event-type list of handlers,
/// invoked concurrently on publish.
/// </summary>
/// <remarks>
/// Handlers are held in an immutable list swapped under a lock on subscribe/unsubscribe, so
/// publishing never blocks and a handler that subscribes or unsubscribes during dispatch
/// cannot corrupt the iteration in flight.
/// </remarks>
internal sealed class EventBus(ILogger<EventBus> logger) : IEventBus
{
    private readonly ConcurrentDictionary<Type, HandlerSet> _handlers = new();

    public async ValueTask PublishAsync<TEvent>(TEvent payload, CancellationToken cancellationToken = default)
        where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (!_handlers.TryGetValue(typeof(TEvent), out var set))
        {
            return;
        }

        var snapshot = set.Current;

        if (snapshot.Count == 0)
        {
            return;
        }

        // Each handler is isolated: a subscriber that throws is logged and dropped for this
        // publish only. A publisher must never inherit a subscriber's failure.
        var tasks = new List<Task>(snapshot.Count);

        foreach (var handler in snapshot)
        {
            tasks.Add(InvokeAsync((Func<TEvent, CancellationToken, Task>)handler, payload, cancellationToken));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task InvokeAsync<TEvent>(
        Func<TEvent, CancellationToken, Task> handler,
        TEvent payload,
        CancellationToken cancellationToken)
    {
        try
        {
            await handler(payload, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "A subscriber to {EventType} threw. Other subscribers are unaffected.",
                typeof(TEvent).FullName);
        }
    }

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler)
        where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);

        var set = _handlers.GetOrAdd(typeof(TEvent), _ => new HandlerSet());
        set.Add(handler);

        return new Subscription(() => set.Remove(handler));
    }

    private sealed class HandlerSet
    {
        private readonly Lock _gate = new();

        public IReadOnlyList<Delegate> Current { get; private set; } = [];

        public void Add(Delegate handler)
        {
            lock (_gate)
            {
                Current = [.. Current, handler];
            }
        }

        public void Remove(Delegate handler)
        {
            lock (_gate)
            {
                Current = [.. Current.Where(h => !h.Equals(handler))];
            }
        }
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                unsubscribe();
            }
        }
    }
}
