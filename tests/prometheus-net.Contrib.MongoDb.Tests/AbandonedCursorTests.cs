using MongoDB.Bson;
using PrometheusNet.Contrib.MongoDb;
using PrometheusNet.Contrib.MongoDb.Handlers;
using PrometheusNet.MongoDb.Events;
using PrometheusNet.MongoDb.Handlers;

namespace PrometheusNet.MongoDb.Tests;

/// <summary>
/// A cursor disposed before its final batch is closed by <c>killCursors</c>; the cursor
/// providers must release their per-operation state (and the gauge) when it succeeds.
/// </summary>
public class AbandonedCursorTests
{
    [Fact]
    public void KillCursors_releases_open_cursor_gauge()
    {
        var provider = new OpenCursorsMetricsProvider();
        const string collection = "abandoned_gauge";

        provider.Handle(FirstBatch(operationId: 9001, collection));
        Assert.Equal(1, provider.OpenCursors.WithLabels(collection, "db").Value);

        provider.Handle(Kill(9001, collection));
        Assert.Equal(0, provider.OpenCursors.WithLabels(collection, "db").Value);

        // Repeated kill must not drive the gauge negative.
        provider.Handle(Kill(9001, collection));
        Assert.Equal(0, provider.OpenCursors.WithLabels(collection, "db").Value);
    }

    [Fact]
    public void KillCursors_records_duration_and_clears_state()
    {
        var provider = new OpenCursorDurationMetricProvider();
        const string collection = "abandoned_duration";

        provider.Handle(FirstBatch(9002, collection));
        Assert.Equal(1, provider.CursorsOpen);

        provider.Handle(Kill(9002, collection));
        Assert.Equal(0, provider.CursorsOpen);
        Assert.Equal(1, provider.OpenCursorDuration.WithLabels(collection, "db").Count);
    }

    [Fact]
    public async Task KillCursors_records_accumulated_document_count()
    {
        var provider = new DocumentCountInCursorMetricProvider();
        const string collection = "abandoned_docs";

        provider.Handle(FirstBatch(9003, collection, documents: 100));
        provider.Handle(Kill(9003, collection));

        provider.DocumentCountInCursor.WithLabels(collection, "db");
        using var stream = new MemoryStream();
        await Prometheus.Metrics.DefaultRegistry.CollectAndExportAsTextAsync(stream, default);
        var exposition = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains($"mongodb_client_cursor_document_count_sum{{target_collection=\"{collection}\",target_db=\"db\"}} 100", exposition);
        Assert.Contains($"mongodb_client_cursor_document_count_count{{target_collection=\"{collection}\",target_db=\"db\"}} 1", exposition);
    }

    [Fact]
    public void GetCursorId_reads_only_getMore()
    {
        Assert.Equal(77L, MongoInstrumentation.GetCursorId("getMore", new BsonDocument("getMore", 77L)));
        Assert.Null(MongoInstrumentation.GetCursorId("find", new BsonDocument("find", "12345")));
        Assert.Null(MongoInstrumentation.GetCursorId("killCursors", new BsonDocument("killCursors", "12345")));
    }

    private static MongoCommandEventSuccess FirstBatch(long operationId, string collection, int documents = 1) =>
        new()
        {
            OperationId = operationId,
            OperationType = MongoOperationType.Find,
            TargetCollection = collection,
            TargetDatabase = "db",
            OperationRawType = "find",
            IsFirstBatch = true,
            BatchDocumentCount = documents,
        };

    private static MongoCommandEventSuccess Kill(long operationId, string collection) =>
        new()
        {
            OperationId = operationId,
            OperationType = MongoOperationType.KillCursors,
            TargetCollection = collection,
            TargetDatabase = "db",
            OperationRawType = "killCursors",
        };
}
