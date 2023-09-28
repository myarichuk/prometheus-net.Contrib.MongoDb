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
    public void Handle(MongoCommandEventSuccess e)
    {
        if (e.OperationType is
            MongoOperationType.Find or
            MongoOperationType.GetMore or
            MongoOperationType.Aggregate)
        {
            if (IsFirstBatch(e.Reply))
            {
                OpenCursors
                    .WithLabels(e.TargetCollection, e.TargetDatabase)
                    .Inc();
            }

            if (IsFinalBatch(e.Reply))
            {
                OpenCursors
                    .WithLabels(e.TargetCollection, e.TargetDatabase)
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
        if (e.OperationType is MongoOperationType.Find or MongoOperationType.GetMore)
        {
            OpenCursors
                .WithLabels(e.TargetCollection, e.TargetDatabase)
                .Dec();
        }
    }

    private static bool IsFinalBatch(Dictionary<string, object> commandReply)
    {
        if (commandReply.TryGetValue("cursor", out var cursorAsObject) &&
            cursorAsObject is Dictionary<string, object> cursor)
        {
            if (cursor.TryGetValue("id", out var cursorIdAsObject) &&
                cursorIdAsObject is long cursorId)
            {
                return cursorId == 0;
            }
        }

        return false;
    }

    private static bool IsFirstBatch(Dictionary<string, object> commandReply)
    {
        if (commandReply.TryGetValue("cursor", out var cursorAsObject) &&
            cursorAsObject is Dictionary<string, object> cursor)
        {
            return cursor.ContainsKey("firstBatch");
        }

        return false;
    }
}
