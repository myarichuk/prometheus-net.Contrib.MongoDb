using Prometheus;
using PrometheusNet.MongoDb.Events;

namespace PrometheusNet.MongoDb.Handlers;

public class CommandDurationProvider: IMetricProvider
{
    /// <summary>
    /// Histogram metric for MongoDB command durations.
    /// </summary>
    public readonly Histogram CommandDuration = Metrics.CreateHistogram(
        "mongodb_client_command_duration",
        "Duration of MongoDB commands (seconds)",
        new HistogramConfiguration
        {
            LabelNames = new[] { "command_type", "status", "target_collection", "target_db" },
        });

    public void Handle(MongoCommandEventStart e)
    {
    }

    public void Handle(MongoCommandEventSuccess e) =>
        CommandDuration
            .WithLabels(e.OperationRawType, "success", e.TargetCollection, e.TargetDatabase)
            .Observe(e.Duration.GetValueOrDefault().TotalSeconds);

    public void Handle(MongoCommandEventFailure e) =>
        CommandDuration
            .WithLabels(e.OperationRawType, "failure", e.TargetCollection, e.TargetDatabase)
            .Observe(e.Duration.GetValueOrDefault().TotalSeconds);
}