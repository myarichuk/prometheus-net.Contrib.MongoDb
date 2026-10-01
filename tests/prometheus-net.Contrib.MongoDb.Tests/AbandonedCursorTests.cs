using MongoDB.Bson;
using PrometheusNet.Contrib.MongoDb;
using PrometheusNet.Contrib.MongoDb.Handlers;
using PrometheusNet.MongoDb.Events;
using PrometheusNet.MongoDb.Handlers;

namespace PrometheusNet.MongoDb.Tests;

/// <summary>
/// A cursor disposed before its final batch is closed by <c>killCursors</c>, which runs
/// as its own driver operation: the kill carries a different operation id than the <c>find</c>
/// that opened the cursor, so cursor providers must release their state by cursor id.
/// </summary>
public class AbandonedCursorTests
{
    [Fact]
    public void KillCursors_releases_open_cursor_gauge()
    {
        var provider = new OpenCursorsMetricsProvider();
        const string collection = "abandoned_gauge";

        provider.Handle(FirstBatch(operationId: 9001, cursorId: 111, collection));
        Assert.Equal(1, provider.OpenCursors.WithLabels(collection, "db").Value);

        // Note the unrelated operation id: the kill is a separate driver operation.
        provider.Handle(Kill(operationId: 9501, cursorId: 111, collection));
        Assert.Equal(0, provider.OpenCursors.WithLabels(collection, "db").Value);

        // Repeated kill must not drive the gauge negative.
        provider.Handle(Kill(operationId: 9502, cursorId: 111, collection));
        Assert.Equal(0, provider.OpenCursors.WithLabels(collection, "db").Value);
    }

    [Fact]
    public void KillCursors_with_unknown_id_leaves_gauge_alone()
    {
        var provider = new OpenCursorsMetricsProvider();
        const string collection = "abandoned_unknown_kill";

        provider.Handle(FirstBatch(operationId: 9011, cursorId: 112, collection));
        provider.Handle(Kill(operationId: 9511, cursorId: 999, collection));

        Assert.Equal(1, provider.OpenCursors.WithLabels(collection, "db").Value);
    }

    [Fact]
    public void Single_batch_cursor_leaves_gauge_at_zero()
    {
        var provider = new OpenCursorsMetricsProvider();
        const string collection = "abandoned_single_batch";

        provider.Handle(SingleBatch(operationId: 9012, collection));

        Assert.Equal(0, provider.OpenCursors.WithLabels(collection, "db").Value);
    }

    [Fact]
    public void KillCursors_records_duration_and_clears_state()
    {
        var provider = new OpenCursorDurationMetricProvider();
        const string collection = "abandoned_duration";

        provider.Handle(FirstBatch(9002, cursorId: 222, collection));
        Assert.Equal(1, provider.CursorsOpen);

        provider.Handle(Kill(9502, cursorId: 222, collection));
        Assert.Equal(0, provider.CursorsOpen);
        Assert.Equal(1, provider.OpenCursorDuration.WithLabels(collection, "db").Count);
    }

    [Fact]
    public async Task KillCursors_records_accumulated_document_count()
    {
        var provider = new DocumentCountInCursorMetricProvider();
        const string collection = "abandoned_docs";

        // Batches may arrive under different operation ids; only the cursor id is stable.
        provider.Handle(FirstBatch(9003, cursorId: 333, collection, documents: 100));
        provider.Handle(NextBatch(operationId: 9004, cursorId: 333, collection, documents: 50));
        provider.Handle(Kill(9503, cursorId: 333, collection));

        provider.DocumentCountInCursor.WithLabels(collection, "db");
        using var stream = new MemoryStream();
        await Prometheus.Metrics.DefaultRegistry.CollectAndExportAsTextAsync(stream, default);
        var exposition = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains($"mongodb_client_cursor_document_count_sum{{target_collection=\"{collection}\",target_db=\"db\"}} 150", exposition);
        Assert.Contains($"mongodb_client_cursor_document_count_count{{target_collection=\"{collection}\",target_db=\"db\"}} 1", exposition);
    }

    [Fact]
    public void GetCursorId_reads_only_getMore()
    {
        Assert.Equal(77L, MongoInstrumentation.GetCursorId("getMore", new BsonDocument("getMore", 77L)));
        Assert.Null(MongoInstrumentation.GetCursorId("find", new BsonDocument("find", "12345")));
        Assert.Null(MongoInstrumentation.GetCursorId("killCursors", new BsonDocument("killCursors", "12345")));
    }

    [Fact]
    public void GetKilledCursorIds_parses_kill_command()
    {
        var kill = new BsonDocument
        {
            { "killCursors", "coll" },
            { "cursors", new BsonArray { 7, 42L } },
        };

        Assert.Equal(new List<long> { 7, 42 }, MongoInstrumentation.GetKilledCursorIds("killCursors", kill));
        Assert.Null(MongoInstrumentation.GetKilledCursorIds("killCursors", new BsonDocument("killCursors", "coll")));
        Assert.Null(MongoInstrumentation.GetKilledCursorIds("killCursors", new BsonDocument { { "killCursors", "coll" }, { "cursors", new BsonArray() } }));
        Assert.Null(MongoInstrumentation.GetKilledCursorIds("find", new BsonDocument("find", "coll")));
    }

    [Fact]
    public void CursorKey_prefers_open_cursor_id()
    {
        Assert.Equal(5L, new MongoCommandEventSuccess { OperationId = 7, CursorId = 5 }.CursorKey);
        Assert.Equal(7L, new MongoCommandEventSuccess { OperationId = 7, CursorId = 0 }.CursorKey);
        Assert.Equal(7L, new MongoCommandEventSuccess { OperationId = 7, CursorId = null }.CursorKey);
    }

    private static MongoCommandEventSuccess FirstBatch(long operationId, long cursorId, string collection, int documents = 1) =>
        new()
        {
            OperationId = operationId,
            OperationType = MongoOperationType.Find,
            TargetCollection = collection,
            TargetDatabase = "db",
            OperationRawType = "find",
            IsFirstBatch = true,
            BatchDocumentCount = documents,
            CursorId = cursorId,
        };

    private static MongoCommandEventSuccess NextBatch(long operationId, long cursorId, string collection, int documents) =>
        new()
        {
            OperationId = operationId,
            OperationType = MongoOperationType.GetMore,
            TargetCollection = collection,
            TargetDatabase = "db",
            OperationRawType = "getMore",
            BatchDocumentCount = documents,
            CursorId = cursorId,
        };

    private static MongoCommandEventSuccess SingleBatch(long operationId, string collection) =>
        new()
        {
            OperationId = operationId,
            OperationType = MongoOperationType.Find,
            TargetCollection = collection,
            TargetDatabase = "db",
            OperationRawType = "find",
            IsFirstBatch = true,
            IsFinalBatch = true,
            BatchDocumentCount = 3,
            CursorId = 0,
        };

    private static MongoCommandEventSuccess Kill(long operationId, long cursorId, string collection) =>
        new()
        {
            OperationId = operationId,
            OperationType = MongoOperationType.KillCursors,
            TargetCollection = collection,
            TargetDatabase = "db",
            OperationRawType = "killCursors",
            KilledCursorIds = new List<long> { cursorId },
        };
}
