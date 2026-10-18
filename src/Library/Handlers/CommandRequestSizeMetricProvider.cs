using Prometheus;
using PrometheusNet.MongoDb;
using PrometheusNet.MongoDb.Events;
using PrometheusNet.MongoDb.Handlers;

namespace PrometheusNet.Contrib.MongoDb.Handlers;

/// <summary>Records the size of each command sent, as serialized on the wire.</summary>
internal sealed class CommandRequestSizeMetricProvider : IMongoDbClientMetricProvider
{
    /// <summary>256 B to 64 MB without gaps: the server's limit is 16 MB per document, 48 MB per message.</summary>
    internal static readonly double[] SizeBuckets = Histogram.ExponentialBuckets(256, 4, 10);

    private readonly MetricChildCache<(string, string, string), HistogramChild> _requestSizeCache;

    public CommandRequestSizeMetricProvider(InstrumentationSession session)
    {
        RequestSize = session.Sink.Histogram(
            "mongodb_client_command_request_size_bytes",
            "Size of commands sent (bytes)",
            SizeBuckets,
            "command_type",
            "target_collection",
            "target_db");
        _requestSizeCache = new(key => RequestSize.WithLabels(key.Item1, key.Item2, key.Item3));
    }

    internal HistogramFamily RequestSize { get; }

    public void Handle(MongoCommandEventStart e) =>
        _requestSizeCache
            .Get((e.OperationRawType, e.TargetCollection, e.TargetDatabase))
            .Observe(e.RawRequestSizeInBytes);
}
