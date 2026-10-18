using System.Diagnostics;
using System.Net;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using PrometheusNet.Contrib.MongoDb;
using PrometheusNet.Contrib.MongoDb.Handlers;
using PrometheusNet.MongoDb.Events;

namespace PrometheusNet.MongoDb.Tests;

/// <summary>
/// Unit tests of <see cref="CursorMetricsProvider"/> on hand-built events, each on a provider
/// and registry of its own.
/// </summary>
public class CursorMetricsProviderTests
{
    private static readonly EndPoint ServerA = new DnsEndPoint("a.example", 27017);

    private static readonly EndPoint ServerB = new DnsEndPoint("b.example", 27017);

    [Fact]
    public void KillCursors_closes_the_cursor_once()
    {
        var provider = NewProvider();
        const string collection = "unit_kill";

        provider.Handle(FirstBatch(cursorId: 111, collection));
        Assert.Equal(1, provider.OpenCursors.WithLabels(collection, "db").Value);

        provider.Handle(Kill(111, collection));
        provider.Handle(Kill(111, collection)); // a repeated kill must not drive the gauge negative

        Assert.Equal(0, provider.OpenCursors.WithLabels(collection, "db").Value);
        Assert.Equal(1, Closed(provider, collection, CursorMetricsProvider.Killed));
        Assert.Equal(1, provider.CursorLifetime.WithLabels(collection, "db").Count);
        Assert.Equal(0, provider.CursorsOpen);
    }

    [Fact]
    public void KillCursors_with_unknown_id_leaves_the_cursor_open()
    {
        var provider = NewProvider();
        const string collection = "unit_unknown_kill";

        provider.Handle(FirstBatch(cursorId: 112, collection));
        provider.Handle(Kill(999, collection));

        Assert.Equal(1, provider.OpenCursors.WithLabels(collection, "db").Value);
        Assert.Equal(0, Closed(provider, collection, CursorMetricsProvider.Killed));
    }

    [Fact]
    public void Failed_kill_still_closes_the_cursor()
    {
        var provider = NewProvider();
        const string collection = "unit_failed_kill";

        provider.Handle(FirstBatch(cursorId: 113, collection));
        provider.Handle(Failure(MongoOperationType.KillCursors, collection, killed: [113]));

        Assert.Equal(0, provider.OpenCursors.WithLabels(collection, "db").Value);
        Assert.Equal(1, Closed(provider, collection, CursorMetricsProvider.Killed));
    }

    [Fact]
    public void Single_batch_reply_opens_no_cursor_but_counts_its_documents()
    {
        var provider = NewProvider();
        const string collection = "unit_single_batch";

        provider.Handle(new MongoCommandEventSuccess
        {
            OperationType = MongoOperationType.Find,
            TargetCollection = collection,
            TargetDatabase = "db",
            IsFirstBatch = true,
            IsFinalBatch = true,
            BatchDocumentCount = 3,
            CursorId = 0,
        });

        Assert.Equal(0, provider.CursorsOpen);
        Assert.Equal(0, provider.OpenCursors.WithLabels(collection, "db").Value);
        Assert.Equal(0, provider.CursorLifetime.WithLabels(collection, "db").Count);
        AssertDocumentCount(provider, collection, sum: 3, count: 1);
    }

    [Fact]
    public void Final_getMore_closes_as_exhausted_with_all_batches_counted()
    {
        var provider = NewProvider();
        const string collection = "unit_exhausted";

        provider.Handle(FirstBatch(cursorId: 222, collection, documents: 100));
        provider.Handle(GetMore(cursorId: 222, collection, documents: 50, final: false));
        provider.Handle(GetMore(cursorId: 222, collection, documents: 7, final: true));

        Assert.Equal(0, provider.OpenCursors.WithLabels(collection, "db").Value);
        Assert.Equal(1, Closed(provider, collection, CursorMetricsProvider.Exhausted));
        Assert.Equal(1, provider.CursorLifetime.WithLabels(collection, "db").Count);
        AssertDocumentCount(provider, collection, sum: 157, count: 1);
    }

    [Fact]
    public void Killed_cursor_reports_the_documents_fetched_so_far()
    {
        var provider = NewProvider();
        const string collection = "unit_kill_docs";

        provider.Handle(FirstBatch(cursorId: 333, collection, documents: 100));
        provider.Handle(GetMore(cursorId: 333, collection, documents: 50, final: false));
        provider.Handle(Kill(333, collection));

        AssertDocumentCount(provider, collection, sum: 150, count: 1);
    }

    [Fact]
    public void GetMore_cursor_not_found_closes_as_server_expired()
    {
        var provider = NewProvider();
        const string collection = "unit_server_expired";

        provider.Handle(FirstBatch(cursorId: 444, collection));
        provider.Handle(Failure(MongoOperationType.GetMore, collection, cursorId: 444, failure: new MongoCursorNotFoundException(Connection(), 444, new BsonDocument())));

        Assert.Equal(0, provider.OpenCursors.WithLabels(collection, "db").Value);
        Assert.Equal(1, Closed(provider, collection, CursorMetricsProvider.ServerExpired));
        Assert.Equal(1, provider.CursorLifetime.WithLabels(collection, "db").Count);
    }

    [Fact]
    public void GetMore_cursor_not_found_command_error_closes_as_server_expired()
    {
        var provider = NewProvider();
        const string collection = "unit_server_expired_code";

        provider.Handle(FirstBatch(cursorId: 445, collection));
        var failure = new MongoCommandException(Connection(), "cursor id 445 not found", new BsonDocument(), new BsonDocument { { "ok", 0 }, { "code", 43 } });
        provider.Handle(Failure(MongoOperationType.GetMore, collection, cursorId: 445, failure: failure));

        Assert.Equal(1, Closed(provider, collection, CursorMetricsProvider.ServerExpired));
    }

    [Fact]
    public void Other_getMore_failure_closes_as_error()
    {
        var provider = NewProvider();
        const string collection = "unit_getmore_error";

        provider.Handle(FirstBatch(cursorId: 446, collection));
        provider.Handle(Failure(MongoOperationType.GetMore, collection, cursorId: 446, failure: new TimeoutException()));

        Assert.Equal(0, provider.OpenCursors.WithLabels(collection, "db").Value);
        Assert.Equal(1, Closed(provider, collection, CursorMetricsProvider.Error));
    }

    [Fact]
    public void Idle_cursor_is_swept_as_abandoned()
    {
        var provider = NewProvider();
        const string collection = "unit_abandoned";
        provider.IdleTimeout = TimeSpan.FromMinutes(1);

        provider.Handle(FirstBatch(cursorId: 555, collection));
        provider.Sweep(Stopwatch.GetTimestamp());
        Assert.Equal(1, provider.CursorsOpen); // not idle yet

        provider.Sweep(Stopwatch.GetTimestamp() + Ticks(TimeSpan.FromMinutes(2)));

        Assert.Equal(0, provider.CursorsOpen);
        Assert.Equal(0, provider.OpenCursors.WithLabels(collection, "db").Value);
        Assert.Equal(1, Closed(provider, collection, CursorMetricsProvider.Abandoned));

        // The kill the application finally sends is not a second close.
        provider.Handle(Kill(555, collection));
        Assert.Equal(0, Closed(provider, collection, CursorMetricsProvider.Killed));
    }

    [Fact]
    public void GetMore_resets_the_idle_clock()
    {
        var provider = NewProvider();
        const string collection = "unit_active";
        provider.IdleTimeout = TimeSpan.FromSeconds(1);

        provider.Handle(FirstBatch(cursorId: 556, collection));
        Thread.Sleep(50);
        provider.Handle(GetMore(cursorId: 556, collection, documents: 1, final: false));

        // Idle for just under the timeout since the getMore, but over it since the open.
        provider.Sweep(Stopwatch.GetTimestamp() + Ticks(TimeSpan.FromSeconds(1)) - Ticks(TimeSpan.FromMilliseconds(25)));

        Assert.Equal(1, provider.CursorsOpen);
    }

    [Fact]
    public void Cursor_that_never_times_out_is_not_swept()
    {
        var provider = NewProvider();
        const string collection = "unit_no_timeout";
        provider.IdleTimeout = TimeSpan.FromSeconds(1);

        var first = FirstBatch(cursorId: 557, collection);
        first.CursorNeverTimesOut = true;
        provider.Handle(first);
        provider.Sweep(Stopwatch.GetTimestamp() + Ticks(TimeSpan.FromHours(1)));

        Assert.Equal(1, provider.CursorsOpen);
        Assert.Equal(0, Closed(provider, collection, CursorMetricsProvider.Abandoned));
    }

    [Fact]
    public void Same_cursor_id_on_two_servers_is_two_cursors()
    {
        var provider = NewProvider();
        const string collection = "unit_two_servers";

        provider.Handle(FirstBatch(cursorId: 666, collection, server: ServerA));
        provider.Handle(FirstBatch(cursorId: 666, collection, server: ServerB));
        Assert.Equal(2, provider.OpenCursors.WithLabels(collection, "db").Value);

        provider.Handle(Kill(666, collection, server: ServerA));

        Assert.Equal(1, provider.OpenCursors.WithLabels(collection, "db").Value);
        Assert.Equal(1, provider.CursorsOpen);
    }

    [Fact]
    public void Cursor_closes_under_the_labels_it_opened_with()
    {
        var provider = NewProvider();
        const string collection = "unit_opener_labels";

        provider.Handle(FirstBatch(cursorId: 777, collection));
        provider.Handle(GetMore(cursorId: 777, "some_other_label", documents: 1, final: true));

        Assert.Equal(0, provider.OpenCursors.WithLabels(collection, "db").Value);
        Assert.Equal(0, provider.OpenCursors.WithLabels("some_other_label", "db").Value);
        Assert.Equal(1, Closed(provider, collection, CursorMetricsProvider.Exhausted));
    }

    [Fact]
    public void Exemption_from_idle_timeout_is_read_from_the_command()
    {
        static bool NeverTimesOut(BsonDocument command, MongoOperationType type) =>
            MongoInstrumentation.CursorNeverTimesOut(command, type, MongoInstrumentation.GetFilterDocument(command, type));

        var plainFind = new BsonDocument { { "find", "c" }, { "filter", new BsonDocument("a", 1) }, { "noCursorTimeout", false } };
        var noTimeout = new BsonDocument { { "find", "c" }, { "filter", new BsonDocument() }, { "noCursorTimeout", true } };
        var tailable = new BsonDocument { { "find", "c" }, { "tailable", true }, { "awaitData", true } };
        var changeStream = new BsonDocument { { "aggregate", "c" }, { "pipeline", new BsonArray { new BsonDocument("$changeStream", new BsonDocument()) } } };
        var aggregate = new BsonDocument { { "aggregate", "c" }, { "pipeline", new BsonArray { new BsonDocument("$match", new BsonDocument()) } } };

        foreach (var materialized in new[] { true, false })
        {
            BsonDocument Shape(BsonDocument d) => materialized ? d : EventReplay.Raw(d);

            Assert.False(NeverTimesOut(Shape(plainFind), MongoOperationType.Find));
            Assert.True(NeverTimesOut(Shape(noTimeout), MongoOperationType.Find));
            Assert.True(NeverTimesOut(Shape(tailable), MongoOperationType.Find));
            Assert.True(NeverTimesOut(Shape(changeStream), MongoOperationType.Aggregate));
            Assert.False(NeverTimesOut(Shape(aggregate), MongoOperationType.Aggregate));
        }
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

    private static CursorMetricsProvider NewProvider() => new(TestMetrics.NewSession());

    private static double Closed(CursorMetricsProvider provider, string collection, string reason) =>
        provider.CursorsClosed.WithLabels(collection, "db", reason).Value;

    private static long Ticks(TimeSpan span) => (long)(span.TotalSeconds * Stopwatch.Frequency);

    private static ConnectionId Connection() =>
        new(new ServerId(new ClusterId(1), ServerA), 1);

    private static void AssertDocumentCount(CursorMetricsProvider provider, string collection, int sum, int count)
    {
        var documents = provider.CursorDocuments.WithLabels(collection, "db");
        Assert.Equal(sum, documents.Sum);
        Assert.Equal(count, documents.Count);
    }

    private static MongoCommandEventSuccess FirstBatch(long cursorId, string collection, int documents = 1, EndPoint? server = null) =>
        new()
        {
            OperationType = MongoOperationType.Find,
            TargetCollection = collection,
            TargetDatabase = "db",
            OperationRawType = "find",
            IsFirstBatch = true,
            BatchDocumentCount = documents,
            CursorId = cursorId,
            ServerEndPoint = server ?? ServerA,
        };

    private static MongoCommandEventSuccess GetMore(long cursorId, string collection, int documents, bool final) =>
        new()
        {
            OperationType = MongoOperationType.GetMore,
            TargetCollection = collection,
            TargetDatabase = "db",
            OperationRawType = "getMore",
            BatchDocumentCount = documents,
            IsFinalBatch = final,
            CursorId = cursorId,
            ServerEndPoint = ServerA,
        };

    private static MongoCommandEventSuccess Kill(long cursorId, string collection, EndPoint? server = null) =>
        new()
        {
            OperationType = MongoOperationType.KillCursors,
            TargetCollection = collection,
            TargetDatabase = "db",
            OperationRawType = "killCursors",
            KilledCursorIds = new List<long> { cursorId },
            ServerEndPoint = server ?? ServerA,
        };

    private static MongoCommandEventFailure Failure(MongoOperationType type, string collection, long? cursorId = null, List<long>? killed = null, Exception? failure = null) =>
        new()
        {
            OperationType = type,
            TargetCollection = collection,
            TargetDatabase = "db",
            CursorId = cursorId,
            KilledCursorIds = killed,
            Failure = failure ?? new TimeoutException(),
            ServerEndPoint = ServerA,
        };
}
