using Prometheus;
using PrometheusNet.MongoDb.Events;

namespace PrometheusNet.MongoDb.Handlers;

/// <summary>
/// Provides functionality for tracking and recording MongoDB command durations.
/// </summary>
internal class CommandDurationMetricProvider : IMetricProvider
{
    private const string SuccessStatus = "success";
    private const string FailureStatus = "failure";

    /// <summary>
    /// A histogram metric that captures the duration of MongoDB commands.
    /// </summary>
    /// <remarks>
    /// The metric includes labels for the type of command, the status (success or failure), target collection, and target database.
    /// Durations are measured in seconds.
    /// </remarks>
    public readonly Histogram CommandDuration = Metrics.CreateHistogram(
        "mongodb_client_command_duration",
        "Duration of MongoDB commands (seconds)",
        new HistogramConfiguration
        {
            LabelNames = new[] { "command_type", "status", "target_collection", "target_db" },
        });

    /// <summary>
    /// Handles the event triggered when a MongoDB command successfully completes.
    /// </summary>
    /// <param name="e">Event information for the successfully completed MongoDB command.</param>
    /// <remarks>
    /// This will record the duration of successful MongoDB commands in the histogram with appropriate labels.
    /// </remarks>
    public void Handle(MongoCommandEventSuccess e) =>
        CommandDuration
            .WithLabels(e.OperationRawType, SuccessStatus, e.TargetCollection, e.TargetDatabase)
            .Observe(e.Duration.GetValueOrDefault().TotalSeconds);

    /// <summary>
    /// Handles the event triggered when a MongoDB command fails.
    /// </summary>
    /// <param name="e">Event information for the failed MongoDB command.</param>
    /// <remarks>
    /// This will record the duration of failed MongoDB commands in the histogram with appropriate labels.
    /// </remarks>
    public void Handle(MongoCommandEventFailure e) =>
        CommandDuration
            .WithLabels(e.OperationRawType, FailureStatus, e.TargetCollection, e.TargetDatabase)
            .Observe(e.Duration.GetValueOrDefault().TotalSeconds);
}