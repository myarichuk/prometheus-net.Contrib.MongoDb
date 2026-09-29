using Prometheus;
using PrometheusNet.MongoDb.Events;

// ReSharper disable UnusedMember.Global

namespace PrometheusNet.MongoDb.Handlers;

/// <summary>
/// Provides functionality to create and update Prometheus metrics related to MongoDB commands.
/// </summary>
internal class CommandErrorsMetricProvider : IMongoDbClientMetricProvider
{
    /// <summary>
    /// A summary metric that captures the total number of command errors in MongoDB operations.
    /// </summary>
    /// <remarks>
    /// The metric includes labels for the type of the command, type of the error, target collection, and target database.
    /// </remarks>
    public static readonly Summary CommandErrors = Metrics.CreateSummary(
        "mongodb_client_command_errors_total",
        "Total number of command errors",
        new SummaryConfiguration
        {
            LabelNames = new[] { "command_type", "error_type", "target_collection", "target_db" },
        });

    private readonly MetricChildCache<(string, string, string, string), Summary.Child> _errorsCache =
        new(key => CommandErrors.WithLabels(key.Item1, key.Item2, key.Item3, key.Item4));

    /// <summary>
    /// Handles the event triggered when a MongoDB command fails.
    /// </summary>
    /// <param name="e">Event information.</param>
    /// <remarks>
    /// This will increment the 'mongodb_client_command_errors_total' metric with appropriate labels.
    /// </remarks>
    public void Handle(MongoCommandEventFailure e)
    {
        _errorsCache
            .Get((e.OperationRawType, e.Failure.GetType().Name, e.TargetCollection, e.TargetDatabase))
            .Observe(1);
    }
}
