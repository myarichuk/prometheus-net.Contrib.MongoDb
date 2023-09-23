namespace PrometheusNet.MongoDb;

internal class EventHub
{
    private readonly Dictionary<Type, List<Delegate>> _handlers = new();
    private readonly ReaderWriterLockSlim _rwLock = new ReaderWriterLockSlim();

    public static EventHub Default { get; } = new EventHub();

    public void Publish<T>(T data)
    {
        _rwLock.EnterReadLock();
        try
        {
            if (_handlers.TryGetValue(typeof(T), out var handlers))
            {
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
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    public void Subscribe<T>(Action<T> handler)
    {
        _rwLock.EnterWriteLock();
        try
        {
            if (!_handlers.ContainsKey(typeof(T)))
            {
                _handlers[typeof(T)] = new List<Delegate>();
            }
            _handlers[typeof(T)].Add(handler);
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    public void Unsubscribe<T>(Action<T> handler)
    {
        _rwLock.EnterWriteLock();
        try
        {
            if (_handlers.TryGetValue(typeof(T), out var handlers))
            {
                handlers.Remove(handler);
                if (handlers.Count == 0)
                {
                    _handlers.Remove(typeof(T));
                }
            }
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    public bool Exists<T>()
    {
        _rwLock.EnterReadLock();
        try
        {
            return _handlers.ContainsKey(typeof(T));
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    // Don't forget to dispose of the ReaderWriterLockSlim when you're done
    public void Dispose()
    {
        _rwLock.Dispose();
    }
}
