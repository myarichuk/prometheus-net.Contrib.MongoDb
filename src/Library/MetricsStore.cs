using Prometheus;

namespace PrometheusNet.MongoDb;

/// <summary>
/// A centralized repository for Prometheus metrics
/// </summary>
public static class MetricsStore
{
    /// <summary>
    /// Counter metric for MongoDB find operations.
    /// </summary>
    /// <remarks>Can be useful for detecting SELECT N+1</remarks>
    public static readonly Counter FindOperations = Metrics.CreateCounter(
        "mongodb_client_find_operations_total",
        "Total number of find operations",
        new CounterConfiguration
        {
            LabelNames = new[] { "target_collection", "target_db" },
        });

    /// <summary>
    /// Counter metric for MongoDB command errors.
    /// </summary>
    public static readonly Counter CommandErrors = Metrics.CreateCounter(
        "mongodb_client_command_errors_total",
        "Total number of command errors",
        new CounterConfiguration
        {
            LabelNames = new[] { "command_type", "error_type", "target_collection", "target_db" },
        });

    /// <summary>
    /// Histogram metric for MongoDB command size.
    /// </summary>
    public static readonly Histogram CommandSize = Metrics.CreateHistogram(
        "mongodb_client_command_size_bytes",
        "Size of MongoDB commands",
        new HistogramConfiguration
        {
            LabelNames = new[] { "command_type", "target_collection", "target_db" },
            Buckets = new[] { 100.0, 500.0, 1000.0, 5000.0, 10000.0, 50000.0, 100000.0 }, // Define your own buckets
        });

    /// <summary>
    /// Histogram metric for MongoDB document count in operations.
    /// </summary>
    public static readonly Histogram CommandDocumentCount = Metrics.CreateHistogram(
        "mongodb_client_command_document_count",
        "Size of MongoDB commands",
        new HistogramConfiguration
        {
            LabelNames = new[] { "command_type", "target_collection", "target_db" },
        });
}