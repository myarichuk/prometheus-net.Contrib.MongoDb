using System.Collections.Concurrent;
using System.Diagnostics;
using Prometheus;
using PrometheusNet.Contrib.MongoDb.Events;
using PrometheusNet.MongoDb.Handlers;

namespace PrometheusNet.Contrib.MongoDb.Handlers;

/// <summary>
/// Provides functionality for tracking and recording MongoDB connection metrics.
/// </summary>
internal class ConnectionMetricsProvider : IMongoDbClientMetricProvider
{
    private readonly ConcurrentDictionary<(int, string), Stopwatch> _connectionDuration = new();

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
    public void Handle(MongoConnectionOpenedEvent @event)
    {
        _connectionDuration.TryAdd(
            (@event.ClusterId, @event.Endpoint), Stopwatch.StartNew());

        ConnectionCreationRate
                .WithLabels(@event.ClusterId.ToString(), @event.Endpoint)
                .Inc();
    }

    /// <summary>
    /// Handles the event triggered when a MongoDB connection is failed.
    /// </summary>
    /// <param name="event">Event information for the failed MongoDB connection.</param>
    public void Handle(MongoConnectionFailedEvent @event)
    {
        if (_connectionDuration.TryRemove(
            (@event.ClusterId, @event.Endpoint), out var stopwatch))
        {
            ConnectionDuration
                    .WithLabels(@event.ClusterId.ToString(), @event.Endpoint)
                    .Observe(stopwatch?.Elapsed.TotalSeconds ?? 0);
        }
    }

    /// <summary>
    /// Handles the event triggered when a MongoDB connection is closed.
    /// </summary>
    /// <param name="event">Event information for the closed MongoDB connection.</param>
    public void Handle(MongoConnectionClosedEvent @event)
    {
        if (_connectionDuration.TryRemove(
            (@event.ClusterId, @event.Endpoint), out var stopwatch))
        {
            ConnectionDuration
                    .WithLabels(@event.ClusterId.ToString(), @event.Endpoint)
                    .Observe(stopwatch?.Elapsed.TotalSeconds ?? 0);
        }
    }
}