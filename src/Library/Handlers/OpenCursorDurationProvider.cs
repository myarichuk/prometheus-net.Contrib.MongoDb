using System.Collections.Concurrent;
using System.Diagnostics;
using Prometheus;
using PrometheusNet.MongoDb.Events;
// ReSharper disable EventExceptionNotDocumented

// ReSharper disable UnusedMember.Global

namespace PrometheusNet.MongoDb.Handlers;

/// <summary>
/// Provides metrics for tracking the duration of open cursors in MongoDB.
/// </summary>
public class OpenCursorDurationProvider : IMetricProvider
{
    /// <summary>
    /// A thread-safe dictionary to store Stopwatch instances for each cursor.
    /// </summary>
    private readonly ConcurrentDictionary<int, Stopwatch> _cursorDurationTimers = new();

    /// <summary>
    /// Histogram metric for tracking the duration a MongoDB cursor is open.
    /// </summary>
    /// <remarks>This is done in seconds</remarks>
    private readonly Histogram _openCursorDuration = Metrics.CreateHistogram(
        "mongodb_client_open_cursors_duration",
        "Duration a MongoDB cursor is open (seconds)",
        new HistogramConfiguration
        {
            LabelNames = new[] { "target_collection", "target_db" },
        });

    /// <summary>
    /// Handles the start event of a MongoDB command.
    /// </summary>
    /// <param name="e">The event data.</param>
    public void Handle(MongoCommandEventStart e)
    {
        if (e.OperationType == MongoOperationType.Find)
        {
            _cursorDurationTimers.TryAdd(e.RequestId, Stopwatch.StartNew());
        }
    }

    /// <summary>
    /// Handles the success event of a MongoDB command.
    /// </summary>
    /// <param name="e">The event data.</param>
    public void Handle(MongoCommandEventSuccess e) =>
        ObserveDuration(e.RequestId, e.TargetCollection, e.TargetDatabase);

    /// <summary>
    /// Handles the failure event of a MongoDB command.
    /// </summary>
    /// <param name="e">The event data.</param>
    public void Handle(MongoCommandEventFailure e) =>
        ObserveDuration(e.RequestId, e.TargetCollection, e.TargetDatabase);

    /// <summary>
    /// Observes and records the duration of an open cursor.
    /// </summary>
    /// <param name="requestId">The request ID.</param>
    /// <param name="targetCollection">The target collection.</param>
    /// <param name="targetDatabase">The target database.</param>
    private void ObserveDuration(int requestId, string targetCollection, string targetDatabase)
    {
        // note: if this is not MongoOperationType.Find, TryRemove() will return false
        if (_cursorDurationTimers.TryRemove(requestId, out var stopwatch))
        {
            stopwatch.Stop();
            var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;

            _openCursorDuration
                .WithLabels(targetCollection, targetDatabase)
                .Observe(elapsedSeconds);
        }
    }
}