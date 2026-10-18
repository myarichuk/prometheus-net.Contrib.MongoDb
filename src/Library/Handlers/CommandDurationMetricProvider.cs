using PrometheusNet.MongoDb.Events;

namespace PrometheusNet.MongoDb.Handlers;

/// <summary>
/// Records how long commands take, from the moment the driver sends them to their reply.
/// The wait for a connection and for server selection comes before that and is recorded
/// by the pool and cluster providers.
/// </summary>
internal sealed class CommandDurationMetricProvider : IMongoDbClientMetricProvider
{
    private const string SuccessStatus = "success";
    private const string FailureStatus = "failure";

    private readonly MetricChildCache<(string, string, string, string), HistogramChild> _durationCache;

    public CommandDurationMetricProvider(InstrumentationSession session)
    {
        CommandDuration = session.Sink.Histogram(
            "mongodb_client_command_duration_seconds",
            "Duration of commands, from sending them to their reply (seconds)",
            session.Options.DurationBuckets,
            "command_type",
            "status",
            "target_collection",
            "target_db");
        _durationCache = new(key => CommandDuration.WithLabels(key.Item1, key.Item2, key.Item3, key.Item4));
    }

    internal HistogramFamily CommandDuration { get; }

    public void Handle(MongoCommandEventSuccess e) => Observe(e, SuccessStatus);

    public void Handle(MongoCommandEventFailure e) => Observe(e, FailureStatus);

    // An unknown duration is skipped, never recorded as 0 s: a fake zero drags every quantile down.
    private void Observe(MongoCommandEvent e, string status)
    {
        if (e.Duration is { } duration)
        {
            _durationCache
                .Get((e.OperationRawType, status, e.TargetCollection, e.TargetDatabase))
                .Observe(duration.TotalSeconds);
        }
    }
}
