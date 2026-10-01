using System.Collections.Concurrent;
using Prometheus;
using PrometheusNet.MongoDb;
using PrometheusNet.MongoDb.Events;
using PrometheusNet.MongoDb.Handlers;

#pragma warning disable SA1118
#pragma warning disable SA1118

// ReSharper disable ComplexConditionExpression
namespace PrometheusNet.Contrib.MongoDb.Handlers;

/// <summary>
/// Provides functionality to track metrics related to MongoDB cursors.
/// Implements the <see cref="IMongoDbClientMetricProvider"/> interface.
/// </summary>
internal class OpenCursorsMetricsProvider : IMongoDbClientMetricProvider
{
    // Keys of cursors we have counted as open (see MongoCommandEvent.CursorKey).
    // The gauge is only moved while an entry is added/removed here, so blind
    // increments/decrements (and negative drift from failures of never-opened
    // cursors) are impossible.
    private readonly ConcurrentDictionary<long, byte> _openCursors = new();

    /// <summary>
    /// A Gauge metric to monitor the number of open MongoDB cursors.
    /// </summary>
    internal readonly Gauge OpenCursors = Metrics.CreateGauge(
        "mongodb_client_open_cursors_count",
        "Number of open cursors",
        new GaugeConfiguration
        {
            LabelNames = new[] { "target_collection", "target_db" },
        });

    /// <summary>
    /// Handles the successful completion event of a MongoDB command.
    /// </summary>
    /// <param name="e">Event data.</param>
    private readonly MetricChildCache<(string, string), Gauge.Child> _openCursorsCache;

    public OpenCursorsMetricsProvider() =>
        _openCursorsCache = new(key => OpenCursors.WithLabels(key.Item1, key.Item2));

    public void Handle(MongoCommandEventSuccess e)
    {
        // killCursors closes cursors abandoned before their final batch. It runs as its
        // own driver operation, so the cursors it closes are matched by id, never by
        // operation id (which belongs to the kill command itself).
        if (e.OperationType is MongoOperationType.KillCursors)
        {
            if (e.KilledCursorIds is { } killedCursorIds)
            {
                foreach (var cursorId in killedCursorIds)
                {
                    if (_openCursors.TryRemove(cursorId, out _))
                    {
                        _openCursorsCache
                            .Get((e.TargetCollection, e.TargetDatabase))
                            .Dec();
                    }
                }
            }

            return;
        }

        if (e.OperationType is
            MongoOperationType.Find or
            MongoOperationType.GetMore or
            MongoOperationType.Aggregate)
        {
            if (e.IsFirstBatch && _openCursors.TryAdd(e.CursorKey, 0))
            {
                _openCursorsCache
                    .Get((e.TargetCollection, e.TargetDatabase))
                    .Inc();
            }

            if (e.IsFinalBatch && _openCursors.TryRemove(e.CursorKey, out _))
            {
                _openCursorsCache
                    .Get((e.TargetCollection, e.TargetDatabase))
                    .Dec();
            }
        }
    }

    /// <summary>
    /// Handles the failure event of a MongoDB command.
    /// </summary>
    /// <param name="e">Event data.</param>
    public void Handle(MongoCommandEventFailure e)
    {
        // failure means cursor won't be open anymore
        // note: if it is a client-side error like timeout, it is possible the cursor will remain open until timeout
        if (e.OperationType is
            MongoOperationType.Find or
            MongoOperationType.GetMore or
            MongoOperationType.Aggregate)
        {
            if (_openCursors.TryRemove(e.CursorKey, out _))
            {
                _openCursorsCache
                    .Get((e.TargetCollection, e.TargetDatabase))
                    .Dec();
            }
        }
    }
}
