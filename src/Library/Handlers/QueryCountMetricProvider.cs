using PrometheusNet.MongoDb.Events;

namespace PrometheusNet.MongoDb.Handlers;

/// <summary>Counts queries: <c>find</c> and <c>aggregate</c> commands, by collection.</summary>
internal sealed class QueryCountMetricProvider : IMongoDbClientMetricProvider
{
    private readonly MetricChildCache<(string, string, string), CounterChild> _queryCountCache;

    public QueryCountMetricProvider(InstrumentationSession session)
    {
        Queries = session.Sink.Counter(
            "mongodb_client_queries_total",
            "Queries (find and aggregate commands) sent",
            "query_type",
            "target_collection",
            "target_db");
        _queryCountCache = new(key => Queries.WithLabels(key.Item1, key.Item2, key.Item3));
    }

    internal CounterFamily Queries { get; }

    public void Handle(MongoCommandEventStart e)
    {
        if (e.OperationType is MongoOperationType.Find or MongoOperationType.Aggregate)
        {
            _queryCountCache
                .Get((e.OperationRawType, e.TargetCollection, e.TargetDatabase))
                .Inc();
        }
    }
}
