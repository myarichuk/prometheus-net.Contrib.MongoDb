using MongoDB.Bson;
using MongoDB.Driver;
using Mongo.Fakes.Server;
using PrometheusNet.Contrib.MongoDb;
using PrometheusNet.Contrib.MongoDb.Handlers;
using PrometheusNet.MongoDb.Handlers;
using static PrometheusNet.MongoDb.Tests.TestMetrics;

namespace PrometheusNet.MongoDb.Tests;

/// <summary>
/// End-to-end cursor, failure and transaction tests against <see cref="MongoFakeServer"/>,
/// which serves real multi-batch cursors (<c>batchSize</c>, <c>getMore</c>, <c>killCursors</c>,
/// idle expiry) and <c>failCommand</c> failpoints. Every test gets its own server, so
/// failpoints never leak, and its own registry, so metrics start at zero.
/// </summary>
public class FakeServerCursorTests
{
    private const string Database = "cursordb";

    private FakeMongo? _fake;

    private CursorMetricsProvider Cursors => _fake!.Provider<CursorMetricsProvider>();

    private CommandDurationMetricProvider CommandDuration => _fake!.Provider<CommandDurationMetricProvider>();

    [Fact]
    public async Task Exhausted_multi_batch_cursor_is_tracked_until_its_last_batch()
    {
        const string collection = "e2e_cursor_exhausted";
        await using var fake = await StartAsync(Database);
        var coll = await fake.SeedAsync(collection, documents: 10);

        using (var cursor = await coll.FindAsync(FilterDefinition<BsonDocument>.Empty, new FindOptions<BsonDocument> { BatchSize = 3 }))
        {
            Assert.True(await cursor.MoveNextAsync());
            Assert.Equal(1, Cursors.OpenCursors.WithLabels(collection, Database).Value);

            while (await cursor.MoveNextAsync())
            {
            }
        }

        Assert.Equal(0, Cursors.OpenCursors.WithLabels(collection, Database).Value);
        Assert.Equal(1, Cursors.CursorLifetime.WithLabels(collection, Database).Count);
        AssertDocumentCount(collection, cursors: 1, documents: 10);
        Assert.Equal(1, Closed(collection, CursorMetricsProvider.Exhausted));
        Assert.Equal(3, CommandDuration.CommandDuration.WithLabels("getMore", "success", collection, Database).Count);
    }

    [Fact]
    public async Task Abandoned_cursor_is_released_by_killCursors()
    {
        const string collection = "e2e_cursor_killed";
        await using var fake = await StartAsync(Database);
        var coll = await fake.SeedAsync(collection, documents: 10);

        using (var cursor = await coll.FindAsync(FilterDefinition<BsonDocument>.Empty, new FindOptions<BsonDocument> { BatchSize = 3 }))
        {
            Assert.True(await cursor.MoveNextAsync());
            Assert.True(await cursor.MoveNextAsync());
        }

        // The driver sends killCursors from Dispose without waiting for the reply.
        await WaitUntilAsync(() => Cursors.OpenCursors.WithLabels(collection, Database).Value == 0);
        Assert.Equal(1, Cursors.CursorLifetime.WithLabels(collection, Database).Count);
        AssertDocumentCount(collection, cursors: 1, documents: 6);
        Assert.Equal(1, Closed(collection, CursorMetricsProvider.Killed));
    }

    [Fact]
    public async Task Single_batch_find_is_not_counted_as_a_cursor()
    {
        const string collection = "e2e_cursor_single_batch";
        await using var fake = await StartAsync(Database);
        var coll = await fake.SeedAsync(collection, documents: 5);

        var all = await coll.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();

        Assert.Equal(5, all.Count);
        Assert.Equal(0, Cursors.OpenCursors.WithLabels(collection, Database).Value);
        Assert.Equal(0, Cursors.CursorLifetime.WithLabels(collection, Database).Count);
        AssertDocumentCount(collection, cursors: 1, documents: 5);
    }

    [Fact]
    public async Task Expired_cursor_is_released_when_getMore_fails()
    {
        const string collection = "e2e_cursor_expired";
        await using var fake = await StartAsync(Database, new MongoFakeServerOptions { CursorTimeout = TimeSpan.FromMilliseconds(50) });
        var coll = await fake.SeedAsync(collection, documents: 10);

        using var cursor = await coll.FindAsync(FilterDefinition<BsonDocument>.Empty, new FindOptions<BsonDocument> { BatchSize = 3 });
        Assert.True(await cursor.MoveNextAsync());
        Assert.Equal(1, Cursors.OpenCursors.WithLabels(collection, Database).Value);

        await Task.Delay(200);
        await Assert.ThrowsAsync<MongoCursorNotFoundException>(() => cursor.MoveNextAsync());

        Assert.Equal(0, Cursors.OpenCursors.WithLabels(collection, Database).Value);
        Assert.Equal(1, Cursors.CursorLifetime.WithLabels(collection, Database).Count);
        Assert.Equal(1, CommandDuration.CommandDuration.WithLabels("getMore", "failure", collection, Database).Count);
        Assert.Equal(1, Closed(collection, CursorMetricsProvider.ServerExpired));
    }

    [Fact]
    public async Task Failed_getMore_releases_the_cursor()
    {
        const string collection = "e2e_cursor_getmore_failpoint";
        await using var fake = await StartAsync(Database);
        var coll = await fake.SeedAsync(collection, documents: 10);

        using var cursor = await coll.FindAsync(FilterDefinition<BsonDocument>.Empty, new FindOptions<BsonDocument> { BatchSize = 3 });
        Assert.True(await cursor.MoveNextAsync());

        // 96 (OperationFailed) is not resumable, so the driver surfaces it instead of retrying.
        await fake.FailCommandAsync("getMore", errorCode: 96);
        await Assert.ThrowsAsync<MongoCommandException>(() => cursor.MoveNextAsync());

        Assert.Equal(0, Cursors.OpenCursors.WithLabels(collection, Database).Value);
        Assert.Equal(1, Cursors.CursorLifetime.WithLabels(collection, Database).Count);
        Assert.Equal(1, Closed(collection, CursorMetricsProvider.Error));
    }

    [Fact]
    public async Task Server_error_is_recorded_as_a_failed_command()
    {
        const string collection = "e2e_find_failpoint";
        await using var fake = await StartAsync(Database);
        var coll = await fake.SeedAsync(collection, documents: 1);

        await fake.FailCommandAsync("find", errorCode: 96);
        await Assert.ThrowsAsync<MongoCommandException>(() => coll.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync());

        Assert.Equal(1, CommandDuration.CommandDuration.WithLabels("find", "failure", collection, Database).Count);
        Assert.Equal(0, CommandDuration.CommandDuration.WithLabels("find", "success", collection, Database).Count);
    }

    [Fact]
    public async Task Committed_transaction_records_commit_duration()
    {
        const string collection = "e2e_transaction";
        await using var fake = await StartAsync(Database, new MongoFakeServerOptions { ReplicaSetName = "rs0" });
        var coll = fake.Collection(collection);

        using (var session = await fake.Client.StartSessionAsync())
        {
            session.StartTransaction();
            await coll.InsertOneAsync(session, new BsonDocument("n", 1));
            await session.CommitTransactionAsync();
        }

        Assert.Equal(1, CommandDuration.CommandDuration.WithLabels("insert", "success", collection, Database).Count);
        Assert.True(CommandDuration.CommandDuration.WithLabels("commitTransaction", "success", string.Empty, "admin").Count >= 1);
    }

    [Fact]
    public async Task Database_level_cursor_is_closed_under_an_empty_collection_label()
    {
        // `aggregate: 1` has no collection; its getMore names `$cmd.aggregate`, which must
        // close the cursor under the same "" label it was opened with.
        const string database = "e2e_db_aggregate";
        await using var fake = await StartAsync(Database);
        var documents = new BsonArray(Enumerable.Range(0, 10).Select(i => new BsonDocument("n", i)));
        var pipeline = new BsonDocument[] { new("$documents", documents) };

        var all = await fake.Client.GetDatabase(database)
            .Aggregate<BsonDocument>(pipeline, new AggregateOptions { BatchSize = 3 })
            .ToListAsync();

        Assert.Equal(10, all.Count);
        Assert.Equal(0, Cursors.OpenCursors.WithLabels(string.Empty, database).Value);
        Assert.Equal(1, Cursors.CursorLifetime.WithLabels(string.Empty, database).Count);
        Assert.Equal(3, CommandDuration.CommandDuration.WithLabels("getMore", "success", string.Empty, database).Count);
        Assert.Equal(0, CommandDuration.CommandDuration.WithLabels("getMore", "success", "$cmd.aggregate", database).Count);
        AssertDocumentCount(string.Empty, cursors: 1, documents: 10, database);
    }

    [Fact]
    public async Task Collectionless_command_is_measured_but_driver_housekeeping_is_not()
    {
        await using (var fake = await StartAsync(Database))
        {
            await fake.Client.ListDatabaseNamesAsync();
            await fake.Client.GetDatabase("e2e_ping").RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));
        }

        Assert.True(CommandDuration.CommandDuration.WithLabels("listDatabases", "success", string.Empty, "admin").Count >= 1);
        Assert.Equal(1, CommandDuration.CommandDuration.WithLabels("ping", "success", string.Empty, "e2e_ping").Count);

        var commands = CommandDuration.CommandDuration.GetAllLabelValues().Select(labels => labels[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("hello", commands);
        Assert.DoesNotContain("isMaster", commands);
        Assert.DoesNotContain("endSessions", commands);
    }

    private async Task<FakeMongo> StartAsync(string database, MongoFakeServerOptions? options = null) =>
        _fake = await FakeMongo.StartAsync(database, options);

    private double Closed(string collection, string reason) =>
        Cursors.CursorsClosed.WithLabels(collection, Database, reason).Value;

    private void AssertDocumentCount(string collection, int cursors, int documents, string database = Database)
    {
        var fetched = Cursors.CursorDocuments.WithLabels(collection, database);
        Assert.Equal(documents, fetched.Sum);
        Assert.Equal(cursors, fetched.Count);
    }
}
