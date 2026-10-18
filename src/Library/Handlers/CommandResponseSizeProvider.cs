using PrometheusNet.MongoDb;
using PrometheusNet.MongoDb.Events;
using PrometheusNet.MongoDb.Handlers;

namespace PrometheusNet.Contrib.MongoDb.Handlers;

/// <summary>Records the size of each reply received, as serialized on the wire.</summary>
internal sealed class CommandResponseSizeProvider : IMongoDbClientMetricProvider
{
    private readonly MetricChildCache<(string, string, string), HistogramChild> _responseSizeCache;

    public CommandResponseSizeProvider(InstrumentationSession session)
    {
        ResponseSize = session.Sink.Histogram(
            "mongodb_client_command_response_size_bytes",
            "Size of command replies received (bytes)",
            CommandRequestSizeMetricProvider.SizeBuckets,
            "command_type",
            "target_collection",
            "target_db");
        _responseSizeCache = new(key => ResponseSize.WithLabels(key.Item1, key.Item2, key.Item3));
    }

    internal HistogramFamily ResponseSize { get; }

    public void Handle(MongoCommandEventSuccess e)
    {
        // Sized straight from the reply document: materializing RawReply into a
        // byte[] here would duplicate result batches (potentially megabytes) that
        // nobody else needs. RawReply stays available for custom providers.
        var replySize = e.ReplyDocument is { } reply
            ? BsonSizeCounter.GetSizeInBytes(reply)
            : 0;

        _responseSizeCache
            .Get((e.OperationRawType, e.TargetCollection, e.TargetDatabase))
            .Observe(replySize);
    }
}
