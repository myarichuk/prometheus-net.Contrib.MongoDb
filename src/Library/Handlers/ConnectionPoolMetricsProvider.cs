using System.Net;
using MongoDB.Driver.Core.Events;
using Prometheus;
using PrometheusNet.MongoDb;
using PrometheusNet.MongoDb.Handlers;

namespace PrometheusNet.Contrib.MongoDb.Handlers;

/// <summary>
/// Connection pool metrics, per server. <c>CommandStartedEvent</c> fires only after a
/// connection is checked out, so command durations never include the wait for a connection:
/// when the pool saturates, that wait is where the latency goes, and it shows up here first.
/// </summary>
internal sealed class ConnectionPoolMetricsProvider : IMongoDbClientMetricProvider
{
    internal const string Timeout = "timeout";
    internal const string ConnectionError = "connection_error";
    internal const string PoolClosed = "pool_closed";

    // 0.1 ms to 6.5 s: a healthy checkout is sub-millisecond, a saturated pool waits up to
    // WaitQueueTimeout (2 minutes by default, but anything past seconds is already an outage).
    internal static readonly double[] WaitBuckets = Histogram.ExponentialBuckets(0.0001, 4, 9);

    private readonly MetricChildCache<EndPoint, HistogramChild> _checkoutDurationCache;
    private readonly MetricChildCache<(EndPoint, ConnectionCheckOutFailedReason), CounterChild> _checkoutFailuresCache;
    private readonly MetricChildCache<EndPoint, GaugeChild> _inUseCache;
    private readonly MetricChildCache<EndPoint, GaugeChild> _connectionsCache;
    private readonly MetricChildCache<EndPoint, CounterChild> _clearedCache;

    public ConnectionPoolMetricsProvider(InstrumentationSession session)
    {
        var sink = session.Sink;
        CheckoutDuration = sink.Histogram(
            "mongodb_client_pool_checkout_duration_seconds",
            "Time spent waiting to check a connection out of the pool (seconds)",
            WaitBuckets,
            "server");
        CheckoutFailures = sink.Counter(
            "mongodb_client_pool_checkout_failures_total",
            "Failed connection checkouts, by reason: timeout (pool exhausted), connection_error or pool_closed",
            "server",
            "reason");
        ConnectionsInUse = sink.UpDownGauge(
            "mongodb_client_pool_connections_in_use",
            "Connections currently checked out of the pool",
            "server");
        Connections = sink.UpDownGauge(
            "mongodb_client_pool_connections",
            "Connections in the pool, in use or idle",
            "server");
        Cleared = sink.Counter(
            "mongodb_client_pool_cleared_total",
            "Times the pool was cleared, after a network error or a server becoming unusable",
            "server");

        _checkoutDurationCache = new(server => CheckoutDuration.WithLabels(ServerLabel.Of(server)));
        _checkoutFailuresCache = new(key => CheckoutFailures.WithLabels(ServerLabel.Of(key.Item1), ReasonLabel(key.Item2)));
        _inUseCache = new(server => ConnectionsInUse.WithLabels(ServerLabel.Of(server)));
        _connectionsCache = new(server => Connections.WithLabels(ServerLabel.Of(server)));
        _clearedCache = new(server => Cleared.WithLabels(ServerLabel.Of(server)));
    }

    internal HistogramFamily CheckoutDuration { get; }

    internal CounterFamily CheckoutFailures { get; }

    internal GaugeFamily ConnectionsInUse { get; }

    internal GaugeFamily Connections { get; }

    internal CounterFamily Cleared { get; }

    public void Handle(ConnectionPoolCheckedOutConnectionEvent e)
    {
        var server = e.ServerId.EndPoint;
        _checkoutDurationCache.Get(server).Observe(e.Duration.TotalSeconds);
        _inUseCache.Get(server).Inc();
    }

    public void Handle(ConnectionPoolCheckingOutConnectionFailedEvent e) =>
        _checkoutFailuresCache.Get((e.ServerId.EndPoint, e.Reason)).Inc();

    public void Handle(ConnectionPoolCheckedInConnectionEvent e) =>
        _inUseCache.Get(e.ServerId.EndPoint).Dec();

    public void Handle(ConnectionPoolAddedConnectionEvent e) =>
        _connectionsCache.Get(e.ServerId.EndPoint).Inc();

    public void Handle(ConnectionPoolRemovedConnectionEvent e) =>
        _connectionsCache.Get(e.ServerId.EndPoint).Dec();

    public void Handle(ConnectionPoolClearedEvent e) =>
        _clearedCache.Get(e.ServerId.EndPoint).Inc();

    private static string ReasonLabel(ConnectionCheckOutFailedReason reason) => reason switch
    {
        ConnectionCheckOutFailedReason.Timeout => Timeout,
        ConnectionCheckOutFailedReason.ConnectionError => ConnectionError,
        ConnectionCheckOutFailedReason.PoolClosed => PoolClosed,
        _ => reason.ToString().ToLowerInvariant(),
    };
}
