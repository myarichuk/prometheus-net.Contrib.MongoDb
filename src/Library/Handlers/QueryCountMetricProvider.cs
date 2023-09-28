using Prometheus;
using PrometheusNet.MongoDb.Events;

namespace PrometheusNet.MongoDb.Handlers;

/// <summary>
/// Provides functionality for tracking and recording MongoDB query counts.
/// </summary>
internal class QueryCountMetricProvider : IMongoDbClientMetricProvider
{
    /// <summary>
    /// A counter metric that captures the count of MongoDB queries.
    /// </summary>
    /// <remarks>
    /// The metric includes labels for the type of query ("find" or "aggregate"), the target collection, and the target database.
    /// </remarks>
    internal readonly Counter QueryCount = Metrics.CreateCounter(
        "mongodb_client_query_count",
        "Count of MongoDB queries",
        new CounterConfiguration
        {
            LabelNames = new[] { "query_type", "target_collection", "target_db" },
        });

    /// <summary>
    /// Handles the event triggered when a MongoDB command is executed.
    /// </summary>
    /// <param name="e">Event information for the executed MongoDB command.</param>
    /// <remarks>
    /// This will increment the counter for "find" and "aggregate" MongoDB queries with appropriate labels.
    /// </remarks>
    public void Handle(MongoCommandEventStart e)
    {
        if (e.OperationType is MongoOperationType.Find or MongoOperationType.Aggregate)
        {
            QueryCount
                .WithLabels(e.OperationRawType, e.TargetCollection, e.TargetDatabase)
                .Inc();
        }
    }
}
