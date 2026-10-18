using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Events;
using Prometheus;
using PrometheusNet.MongoDb;
using PrometheusNet.MongoDb.Handlers;

namespace PrometheusNet.Contrib.MongoDb.Handlers;

/// <summary>
/// Connection churn per server: connections opened, and how long each one lived. Tracked by
/// the driver's <see cref="ConnectionId"/>, unique per physical connection; tracking by
/// server would collapse concurrent connections into one entry and lose close events.
/// </summary>
internal sealed class ConnectionMetricsProvider : IMongoDbClientMetricProvider
{
    // 1 s to 4.5 h: the pool's idle and lifetime limits are minutes to hours, and a short
    // lifetime means churn (network errors, pool clears).
    private static readonly double[] LifetimeBuckets = Histogram.ExponentialBuckets(1, 4, 8);

    private readonly ConcurrentDictionary<ConnectionId, long> _openedTimestamps = new();

    private readonly MetricChildCache<EndPoint, CounterChild> _openedCache;

    private readonly MetricChildCache<EndPoint, HistogramChild> _lifetimeCache;

    public ConnectionMetricsProvider(InstrumentationSession session)
    {
        ConnectionsOpened = session.Sink.Counter(
            "mongodb_client_connections_opened_total",
            "Connections opened to the server",
            "server");
        ConnectionLifetime = session.Sink.Histogram(
            "mongodb_client_connection_lifetime_seconds",
            "How long connections stayed open, until closed or failed (seconds)",
            LifetimeBuckets,
            "server");
        _openedCache = new(server => ConnectionsOpened.WithLabels(ServerLabel.Of(server)));
        _lifetimeCache = new(server => ConnectionLifetime.WithLabels(ServerLabel.Of(server)));
    }

    internal CounterFamily ConnectionsOpened { get; }

    internal HistogramFamily ConnectionLifetime { get; }

    public void Handle(ConnectionOpenedEvent e)
    {
        _openedTimestamps.TryAdd(e.ConnectionId, Stopwatch.GetTimestamp());
        _openedCache.Get(e.ServerId.EndPoint).Inc();
    }

    // A connection that fails is closed afterwards too; whichever comes first ends it.
    public void Handle(ConnectionFailedEvent e) => Closed(e.ConnectionId, e.ServerId.EndPoint);

    public void Handle(ConnectionClosedEvent e) => Closed(e.ConnectionId, e.ServerId.EndPoint);

    private void Closed(ConnectionId connection, EndPoint server)
    {
        if (_openedTimestamps.TryRemove(connection, out var openedTimestamp))
        {
            _lifetimeCache.Get(server).Observe((Stopwatch.GetTimestamp() - openedTimestamp) / (double)Stopwatch.Frequency);
        }
    }
}
