using System.Collections.Concurrent;
using System.Diagnostics;
using Prometheus;
using PrometheusNet.MongoDb;
using PrometheusNet.Contrib.MongoDb.Events;
using PrometheusNet.MongoDb.Handlers;

namespace PrometheusNet.Contrib.MongoDb.Handlers;

/// <summary>
/// Provides functionality for tracking and recording MongoDB connection metrics.
/// Connections are tracked by driver-assigned <see cref="MongoDB.Driver.Core.Connections.ConnectionId"/>
/// (unique per physical connection). Tracking by endpoint would collapse concurrent
/// connections to the same server into one entry and lose close events.
/// </summary>
internal class ConnectionMetricsProvider : IMongoDbClientMetricProvider
{
    private readonly ConcurrentDictionary<MongoDB.Driver.Core.Connections.ConnectionId, long> _connectionStartTimestamps = new();

    public ConnectionMetricsProvider()
    {
        _creationCache = new(key => ConnectionCreationRate.WithLabels(key.Item1, key.Item2));
        _durationCache = new(key => ConnectionDuration.WithLabels(key.Item1, key.Item2));
    }

    /// <summary>
    /// A counter metric that captures the rate of MongoDB connection creations.
    /// </summary>
    internal readonly Counter ConnectionCreationRate = Metrics.CreateCounter(
        "mongodb_client_connection_creation_rate",
        "Rate of MongoDB connection creations",
        new CounterConfiguration
        {
            LabelNames = new[] { "cluster_id", "end_point" },
        });

    /// <summary>
    /// A histogram metric that captures the duration it takes to close MongoDB connections.
    /// </summary>
    internal readonly Histogram ConnectionDuration = Metrics.CreateHistogram(
        "mongodb_client_connection_duration",
        "Duration it takes to close MongoDB connections (seconds)",
        new HistogramConfiguration
        {
            LabelNames = new[] { "cluster_id", "end_point" },
        });

    /// <summary>
    /// Handles the event triggered when a MongoDB connection is created.
    /// </summary>
    /// <param name="event">Event information for the created MongoDB connection.</param>
    private readonly MetricChildCache<(string, string), Counter.Child> _creationCache;

    private readonly MetricChildCache<(string, string), Histogram.Child> _durationCache;

    public void Handle(MongoConnectionOpenedEvent @event)
    {
        _connectionStartTimestamps.TryAdd(@event.ConnectionId, Stopwatch.GetTimestamp());

        _creationCache
                .Get((@event.ClusterId.ToString(), @event.Endpoint))
                .Inc();
    }

    /// <summary>
    /// Handles the event triggered when a MongoDB connection is failed.
    /// </summary>
    /// <param name="event">Event information for the failed MongoDB connection.</param>
    public void Handle(MongoConnectionFailedEvent @event)
    {
        if (_connectionStartTimestamps.TryRemove(@event.ConnectionId, out var startTimestamp))
        {
            _durationCache
                    .Get((@event.ClusterId.ToString(), @event.Endpoint))
                    .Observe(GetElapsedSeconds(startTimestamp));
        }
    }

    /// <summary>
    /// Handles the event triggered when a MongoDB connection is closed.
    /// </summary>
    /// <param name="event">Event information for the closed MongoDB connection.</param>
    public void Handle(MongoConnectionClosedEvent @event)
    {
        if (_connectionStartTimestamps.TryRemove(@event.ConnectionId, out var startTimestamp))
        {
            _durationCache
                    .Get((@event.ClusterId.ToString(), @event.Endpoint))
                    .Observe(GetElapsedSeconds(startTimestamp));
        }
    }

    private static double GetElapsedSeconds(long startTimestamp) =>
        (Stopwatch.GetTimestamp() - startTimestamp) / (double)Stopwatch.Frequency;
}
