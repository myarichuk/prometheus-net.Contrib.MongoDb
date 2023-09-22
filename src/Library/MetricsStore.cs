using Prometheus;

namespace PrometheusNet.MongoDb;

/// <summary>
/// A centralized repository for Prometheus metrics
/// </summary>
public static class MetricsStore
{
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
}