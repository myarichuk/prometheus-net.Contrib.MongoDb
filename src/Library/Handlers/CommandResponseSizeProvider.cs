using Prometheus;
using PrometheusNet.MongoDb.Events;
using PrometheusNet.MongoDb.Handlers;
// ReSharper disable ComplexConditionExpression

/// <summary>
/// Provides metrics related to the size of MongoDB command responses.
/// </summary>
internal class CommandResponseSizeProvider : IMetricProvider
{
    /// <summary>
    /// Histogram metric to measure the size of MongoDB command responses.
    /// Buckets are configured to capture various ranges of response sizes.
    /// </summary>
    public static readonly Histogram CommandResponseSize = Metrics.CreateHistogram(
        "mongodb_client_command_response_size", // Metric name
        "Size of the MongoDB command responses (in bytes)", // Help text
        new HistogramConfiguration
        {
            LabelNames = new[] { "command_type", "target_collection", "target_db" },
            Buckets = new[] { 512.0, 1024.0, 100.0 * 1024, 1024.0 * 1024.0 },
        });

    /// <summary>
    /// Handles the successful completion of a MongoDB command event.
    /// </summary>
    /// <param name="e">The event data.</param>
    public void Handle(MongoCommandEventSuccess e)
    {
        var replySize = e.RawReply.Length;

        CommandResponseSize
            .WithLabels(e.OperationRawType, e.TargetCollection, e.TargetDatabase)
            .Observe(replySize);
    }
}
