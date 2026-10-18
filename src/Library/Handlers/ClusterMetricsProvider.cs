using System.Net;
using System.Text;
using MongoDB.Driver.Core.Events;
using MongoDB.Driver.Core.Servers;
using PrometheusNet.MongoDb;
using PrometheusNet.MongoDb.Handlers;

namespace PrometheusNet.Contrib.MongoDb.Handlers;

/// <summary>
/// Server selection and server monitoring metrics. Selection happens before a connection is
/// checked out and before the command starts, so neither the pool nor the command metrics
/// see it: during an election or with no reachable primary, the application waits here.
/// </summary>
internal sealed class ClusterMetricsProvider : IMongoDbClientMetricProvider
{
    private readonly MetricChildCache<string, HistogramChild> _selectionDurationCache;
    private readonly MetricChildCache<string, CounterChild> _selectionFailuresCache;
    private readonly MetricChildCache<EndPoint, GaugeChild> _rttCache;
    private readonly MetricChildCache<(EndPoint, ServerType), CounterChild> _typeChangesCache;
    private readonly MetricChildCache<EndPoint, CounterChild> _heartbeatFailuresCache;

    public ClusterMetricsProvider(InstrumentationSession session)
    {
        var sink = session.Sink;
        SelectionDuration = sink.Histogram(
            "mongodb_client_server_selection_duration_seconds",
            "Time spent selecting a server for an operation (seconds)",
            ConnectionPoolMetricsProvider.WaitBuckets,
            "operation");
        SelectionFailures = sink.Counter(
            "mongodb_client_server_selection_failures_total",
            "Operations for which no suitable server was found in time",
            "operation");
        RoundTripTime = sink.Gauge(
            "mongodb_client_server_rtt_seconds",
            "The driver's averaged round trip time to the server, as used for server selection (seconds)",
            "server");
        TypeChanges = sink.Counter(
            "mongodb_client_server_type_changes_total",
            "Times a server changed type (primary, secondary, unknown, ...), e.g. on elections and outages",
            "server",
            "new_type");
        HeartbeatFailures = sink.Counter(
            "mongodb_client_server_heartbeat_failures_total",
            "Failed server monitoring checks",
            "server");

        _selectionDurationCache = new(operation => SelectionDuration.WithLabels(operation));
        _selectionFailuresCache = new(operation => SelectionFailures.WithLabels(operation));
        _rttCache = new(server => RoundTripTime.WithLabels(ServerLabel.Of(server)));
        _typeChangesCache = new(key => TypeChanges.WithLabels(ServerLabel.Of(key.Item1), TypeLabel(key.Item2)));
        _heartbeatFailuresCache = new(server => HeartbeatFailures.WithLabels(ServerLabel.Of(server)));
    }

    internal HistogramFamily SelectionDuration { get; }

    internal CounterFamily SelectionFailures { get; }

    internal GaugeFamily RoundTripTime { get; }

    internal CounterFamily TypeChanges { get; }

    internal CounterFamily HeartbeatFailures { get; }

    public void Handle(ClusterSelectedServerEvent e) =>
        _selectionDurationCache.Get(e.OperationName ?? string.Empty).Observe(e.Duration.TotalSeconds);

    public void Handle(ClusterSelectingServerFailedEvent e) =>
        _selectionFailuresCache.Get(e.OperationName ?? string.Empty).Inc();

    public void Handle(ServerHeartbeatFailedEvent e) =>
        _heartbeatFailuresCache.Get(e.ServerId.EndPoint).Inc();

    public void Handle(ServerDescriptionChangedEvent e)
    {
        var server = e.ServerId.EndPoint;
        var description = e.NewDescription;

        // Heartbeat durations are not round trip times: streaming (awaited) heartbeats include
        // the time the server held the request. The driver's own average excludes them.
        if (description.AverageRoundTripTime > TimeSpan.Zero)
        {
            _rttCache.Get(server).Set(description.AverageRoundTripTime.TotalSeconds);
        }

        // Descriptions change on every RTT update too; only a type change is a topology change.
        if (e.OldDescription?.Type != description.Type)
        {
            _typeChangesCache.Get((server, description.Type)).Inc();
        }
    }

    // ReplicaSetPrimary -> replica_set_primary, built once per (server, type) by the cache.
    private static string TypeLabel(ServerType type)
    {
        var name = type.ToString();
        var label = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
            {
                label.Append('_');
            }

            label.Append(char.ToLowerInvariant(name[i]));
        }

        return label.ToString();
    }
}
