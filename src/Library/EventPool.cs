using PrometheusNet.MongoDb.Events;

namespace PrometheusNet.MongoDb;

/// <summary>
/// Single-slot per-thread pool for metric event shells. Events are created on every
/// command and die in Gen0 right after dispatch; pooling removes them from the
/// allocation profile entirely.
/// <para/>
/// The pool is only safe because dispatch is synchronous: rent, populate, publish and
/// return all happen on the same thread with no awaits in between, so a slot can never
/// be re-entered or leak across threads. Returned events are <see cref="MongoCommandEvent.Reset"/>
/// before they are stored.
/// <para/>
/// Consequence, stated loudly: <b>never retain an event object</b> (or its document
/// references) beyond the handler callback. A retained event will be silently overwritten
/// by the next command on that thread.
/// </summary>
internal static class EventPool<T>
    where T : MongoCommandEvent, new()
{
    [ThreadStatic]
    private static T? _cached;

    public static T Rent()
    {
        var rented = _cached;
        _cached = null;
        return rented ?? new T();
    }

    public static void Return(T @event)
    {
        @event.Reset();
        _cached = @event;
    }
}
