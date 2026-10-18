using System.Collections.Concurrent;

namespace PrometheusNet.MongoDb;

/// <summary>
/// A minimal in-process event bus that dispatches metric events to providers.
/// Publishing is lock-free (a single volatile array read per event type);
/// subscription changes are rare and pay for a copy-on-write update.
/// Handlers run synchronously on the publisher's thread and are invoked
/// outside of any lock, so a slow handler can never block other publishers.
/// </summary>
internal class EventHub
{
    private readonly ConcurrentDictionary<Type, Delegate[]> _handlers = new();

    private readonly CounterFamily? _handlerErrors;

    private readonly ConcurrentDictionary<Type, CounterChild> _handlerErrorsByProvider = new();

    /// <param name="handlerErrors">Counts provider exceptions by provider type name; null to only report them to the EventSource.</param>
    public EventHub(CounterFamily? handlerErrors = null) => _handlerErrors = handlerErrors;

    private void ReportHandlerFailure(Delegate handler, Type eventType, Exception exception)
    {
        try
        {
            var providerType = handler.Target?.GetType() ?? handler.Method.DeclaringType ?? typeof(EventHub);
            if (_handlerErrors is { } handlerErrors)
            {
                _handlerErrorsByProvider
                    .GetOrAdd(providerType, type => handlerErrors.WithLabels(type.Name))
                    .Inc();
            }

            InstrumentationEventSource.Log.HandlerFailed(providerType.Name, eventType.Name, exception);
        }
        catch
        {
            // Reporting is best effort; it must not throw into the driver either.
        }
    }

    public void Publish<T>(T data)
    {
        if (!_handlers.TryGetValue(typeof(T), out var handlers) || handlers.Length == 0)
        {
            return;
        }

        foreach (var handler in handlers)
        {
            if (handler is Action<T> action)
            {
                try
                {
                    action(data);
                }
                catch (Exception ex)
                {
                    // A faulty provider must never break the application's database call,
                    // and must not write to stdout either: count it, and report it (rate
                    // limited) to whoever listens on the EventSource.
                    ReportHandlerFailure(handler, typeof(T), ex);
                }
            }
        }
    }

    public void Subscribe<T>(Action<T> handler) => Subscribe(typeof(T), handler);

    internal void Subscribe(Type eventType, Delegate handler) =>
        _handlers.AddOrUpdate(
            eventType,
            [handler],
            (_, existing) =>
            {
                if (Array.IndexOf(existing, handler) >= 0)
                {
                    return existing;
                }

                var updated = new Delegate[existing.Length + 1];
                Array.Copy(existing, updated, existing.Length);
                updated[existing.Length] = handler;
                return updated;
            });

    public void Unsubscribe<T>(Action<T> handler) => Unsubscribe(typeof(T), handler);

    internal void Unsubscribe(Type eventType, Delegate handler)
    {
        while (true)
        {
            if (!_handlers.TryGetValue(eventType, out var existing))
            {
                return;
            }

            var index = Array.IndexOf(existing, handler);
            if (index < 0)
            {
                return;
            }

            Delegate[] updated;
            if (existing.Length == 1)
            {
                updated = [];
            }
            else
            {
                updated = new Delegate[existing.Length - 1];
                Array.Copy(existing, 0, updated, 0, index);
                Array.Copy(existing, index + 1, updated, index, existing.Length - index - 1);
            }

            if (existing.Length == 1)
            {
                // Removes the entry only if nobody changed it since our snapshot.
                if (((ICollection<KeyValuePair<Type, Delegate[]>>)_handlers).Remove(
                    new KeyValuePair<Type, Delegate[]>(eventType, existing)))
                {
                    return;
                }
            }
            else if (_handlers.TryUpdate(eventType, updated, existing))
            {
                return;
            }

            // Lost a race with a concurrent subscribe/unsubscribe; retry against the new snapshot.
        }
    }

    public bool Exists<T>() =>
        _handlers.TryGetValue(typeof(T), out var handlers) && handlers.Length > 0;
}
