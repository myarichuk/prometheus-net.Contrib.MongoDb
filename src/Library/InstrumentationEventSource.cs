using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace PrometheusNet.MongoDb;

/// <summary>
/// Diagnostics for the instrumentation itself, without a logging dependency. Listen with
/// <c>dotnet-trace</c>/<c>dotnet-monitor</c> or an in-process <see cref="EventListener"/>
/// on <c>PrometheusNet-Contrib-MongoDb</c>.
/// </summary>
[EventSource(Name = "PrometheusNet-Contrib-MongoDb")]
internal sealed class InstrumentationEventSource : EventSource
{
    public static readonly InstrumentationEventSource Log = new();

    // A handler that throws on every command would otherwise flood the listener.
    private static readonly long MinTicksBetweenReports = Stopwatch.Frequency;

    private readonly ConcurrentDictionary<string, long> _lastReported = new();

    private InstrumentationEventSource()
    {
    }

    [NonEvent]
    public void HandlerFailed(string provider, string eventType, Exception exception)
    {
        if (!IsEnabled(EventLevel.Error, EventKeywords.All))
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var last = _lastReported.GetOrAdd(provider, 0);
        if (last != 0 && now - last < MinTicksBetweenReports)
        {
            return;
        }

        // Losing this race only means one extra report.
        _lastReported[provider] = now;
        HandlerFailed(provider, eventType, exception.ToString());
    }

    [Event(1, Level = EventLevel.Error, Message = "Metric provider {0} threw while handling {1}: {2}")]
    private void HandlerFailed(string provider, string eventType, string exception) =>
        WriteEvent(1, provider, eventType, exception);
}
