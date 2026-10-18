using MongoDB.Bson;
using MongoDB.Driver;
using PrometheusNet.Contrib.MongoDb.Handlers;

namespace PrometheusNet.MongoDb.Tests;

/// <summary>
/// Cursor lifecycles replayed through the instrumentation's real entry points with real
/// driver events (see <see cref="EventReplay"/>), each into an instrumentation of its own.
/// </summary>
public class CursorLifecycleReplayTests
{
    private readonly EventReplay Replay = new();

    [Fact]
    public void KillCursors_closes_a_multi_batch_cursor()
    {
        const string collection = "replay_kill";
        const long cursorId = 7_000_001;

        Replay.Find(collection, operationId: 71, cursorId, documents: 101);
        Assert.Equal(1, Cursors.OpenCursors.WithLabels(collection, "db").Value);

        Replay.GetMore(collection, operationId: 71, cursorId, replyCursorId: cursorId, documents: 50);

        // The application disposed the cursor early: the driver kills it as a new operation.
        Replay.KillCursors(collection, operationId: 72, cursorId);

        Assert.Equal(0, Cursors.OpenCursors.WithLabels(collection, "db").Value);
        Assert.Equal(1, Cursors.CursorLifetime.WithLabels(collection, "db").Count);
        AssertDocumentCount(collection, sum: 151, count: 1);
    }

    [Fact]
    public void Exhausted_cursor_closes_on_final_getMore()
    {
        const string collection = "replay_exhausted";
        const long cursorId = 7_000_002;

        Replay.Find(collection, operationId: 81, cursorId, documents: 101);
        Replay.GetMore(collection, operationId: 81, cursorId, replyCursorId: 0, documents: 9);

        Assert.Equal(0, Cursors.OpenCursors.WithLabels(collection, "db").Value);
        Assert.Equal(1, Cursors.CursorLifetime.WithLabels(collection, "db").Count);
        AssertDocumentCount(collection, sum: 110, count: 1);
    }

    [Fact]
    public void Single_batch_reply_is_not_an_open_cursor()
    {
        const string collection = "replay_single_batch";

        Replay.Find(collection, operationId: 91, cursorId: 0, documents: 5);

        Assert.Equal(0, Cursors.OpenCursors.WithLabels(collection, "db").Value);

        // No ~0 s pseudo-cursor in the lifetime histogram...
        Assert.Equal(0, Cursors.CursorLifetime.WithLabels(collection, "db").Count);

        // ...but the documents it returned still count.
        AssertDocumentCount(collection, sum: 5, count: 1);
    }

    [Fact]
    public void Failed_getMore_releases_the_cursor()
    {
        const string collection = "replay_cursor_not_found";
        const long cursorId = 7_000_003;

        Replay.Find(collection, operationId: 101, cursorId, documents: 101);
        Replay.Fail(
            "getMore",
            new BsonDocument { { "getMore", cursorId }, { "collection", collection }, { "$db", "db" } },
            new MongoException("CursorNotFound"),
            operationId: 101);

        Assert.Equal(0, Cursors.OpenCursors.WithLabels(collection, "db").Value);
    }

    [Fact]
    public void Failed_kill_still_releases_the_cursor()
    {
        const string collection = "replay_failed_kill";
        const long cursorId = 7_000_004;

        Replay.Find(collection, operationId: 111, cursorId, documents: 101);
        Replay.Fail(
            "killCursors",
            new BsonDocument { { "killCursors", collection }, { "cursors", new BsonArray { cursorId } }, { "$db", "db" } },
            new MongoException("network"),
            operationId: 112);

        Assert.Equal(0, Cursors.OpenCursors.WithLabels(collection, "db").Value);
        Assert.Equal(1, Cursors.CursorLifetime.WithLabels(collection, "db").Count);
    }

    [Fact]
    public void Non_find_cursor_is_tracked_like_any_other()
    {
        // listIndexes opens a cursor too; all providers must agree on its lifecycle.
        const string collection = "replay_list_indexes";
        const long cursorId = 7_000_005;

        Replay.Succeed(
            "listIndexes",
            new BsonDocument { { "listIndexes", collection }, { "$db", "db" } },
            Replay.CursorReply("firstBatch", collection, cursorId, documents: 2),
            operationId: 121);
        Assert.Equal(1, Cursors.OpenCursors.WithLabels(collection, "db").Value);

        Replay.KillCursors(collection, operationId: 122, cursorId);

        Assert.Equal(0, Cursors.OpenCursors.WithLabels(collection, "db").Value);
        Assert.Equal(1, Cursors.CursorLifetime.WithLabels(collection, "db").Count);
    }

    private CursorMetricsProvider Cursors => Replay.Provider<CursorMetricsProvider>();

    private void AssertDocumentCount(string collection, int sum, int count)
    {
        var documents = Cursors.CursorDocuments.WithLabels(collection, "db");
        Assert.Equal(sum, documents.Sum);
        Assert.Equal(count, documents.Count);
    }
}
