using System.Diagnostics;

namespace PrometheusNet.MongoDb;

/// <summary>
/// Keeps exemplars honest. prometheus-net stamps each counter increment and histogram
/// observation with the trace of <see cref="Activity.Current"/> (as do OpenTelemetry's Meter
/// exemplars), which is right for a command: the driver raises its events in the
/// operation's own context. It is wrong for work the operation did not cause: a sweep run
/// during a scrape would link to the scrape's request, and a monitoring thread carries
/// whatever context it captured when it started, long ago.
/// </summary>
internal static class TraceContext
{
    /// <summary>Runs <paramref name="action"/> with no current activity, so it records no exemplars.</summary>
    public static void Detached<TState>(Action<TState> action, TState state)
    {
        var current = Activity.Current;
        if (current is null)
        {
            action(state);
            return;
        }

        Activity.Current = null;
        try
        {
            action(state);
        }
        finally
        {
            Activity.Current = current;
        }
    }

    /// <summary>A handler that never records exemplars: for events no operation causes.</summary>
    public static Action<T> Detached<T>(Action<T> handler) => e => Detached(handler, e);

    /// <summary>
    /// A handler that keeps the current activity unless it has already stopped. A stopped
    /// one was captured by a background thread (pool maintenance, say) from an operation that
    /// has finished, and its trace has nothing to do with this event. .NET does not let a
    /// stopped activity become current again, so that context carries on with none.
    /// </summary>
    public static Action<T> Live<T>(Action<T> handler) =>
        e =>
        {
            if (Activity.Current is { IsStopped: true })
            {
                Detached(handler, e);
            }
            else
            {
                handler(e);
            }
        };
}
