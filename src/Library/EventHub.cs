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

    public static EventHub Default { get; } = new EventHub();

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
                    // Handle the exception as you see fit
                    Console.WriteLine($"An error occurred while publishing: {ex}");
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
