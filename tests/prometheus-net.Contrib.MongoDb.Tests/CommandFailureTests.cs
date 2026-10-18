using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using PrometheusNet.Contrib.MongoDb.Handlers;
using PrometheusNet.MongoDb.Handlers;

namespace PrometheusNet.MongoDb.Tests;

/// <summary>
/// Error labels, retries and orphaned commands: replayed driver events for exact cases,
/// <see cref="Mongo.Fakes.Server.MongoFakeServer"/> failpoints for what the driver really does.
/// </summary>
public class CommandFailureTests
{
    private const string Database = "failuredb";

    private static readonly ConnectionId Connection =
        new(new ServerId(new ClusterId(1), new DnsEndPoint("failures", 27017)), 1);

    private static long _nextOperationId = 7_000_000;

    private static long NextOperationId() => Interlocked.Increment(ref _nextOperationId);

    [Fact]
    public void Failures_are_categorized()
    {
        Assert.Equal(CommandErrorsMetricProvider.Timeout, CommandErrorsMetricProvider.Category(new TimeoutException()));
        Assert.Equal(CommandErrorsMetricProvider.Timeout, CommandErrorsMetricProvider.Category(new MongoExecutionTimeoutException(Connection, "max time")));
        Assert.Equal(CommandErrorsMetricProvider.Timeout, CommandErrorsMetricProvider.Category(CommandError(50, "MaxTimeMSExpired")));
        Assert.Equal(CommandErrorsMetricProvider.Network, CommandErrorsMetricProvider.Category(new MongoConnectionException(Connection, "reset")));
        Assert.Equal(CommandErrorsMetricProvider.Network, CommandErrorsMetricProvider.Category(new SocketException()));
        Assert.Equal(CommandErrorsMetricProvider.Server, CommandErrorsMetricProvider.Category(CommandError(11000, "DuplicateKey")));
        Assert.Equal(CommandErrorsMetricProvider.Client, CommandErrorsMetricProvider.Category(new InvalidOperationException()));
    }

    [Fact]
    public void Command_error_without_a_reply_is_labelled_without_throwing()
    {
        // The driver's three-argument constructor leaves no reply: Code and CodeName throw.
        var noReply = new MongoCommandException(Connection, "failed", new BsonDocument("find", "c"));

        Assert.Equal(string.Empty, CommandErrorsMetricProvider.CodeName(noReply));
        Assert.Equal(CommandErrorsMetricProvider.Server, CommandErrorsMetricProvider.Category(noReply));
        Assert.False(CommandErrorsMetricProvider.IsRetryable(noReply));
    }

    [Fact]
    public void Code_name_and_retryability_come_from_the_server_reply()
    {
        var notPrimary = CommandError(10107, "NotWritablePrimary");
        notPrimary.AddErrorLabel("RetryableWriteError");
        var transient = CommandError(112, "WriteConflict");
        transient.AddErrorLabel("TransientTransactionError");

        Assert.Equal("NotWritablePrimary", CommandErrorsMetricProvider.CodeName(notPrimary));
        Assert.True(CommandErrorsMetricProvider.IsRetryable(notPrimary));
        Assert.True(CommandErrorsMetricProvider.IsRetryable(transient));
        Assert.False(CommandErrorsMetricProvider.IsRetryable(CommandError(11000, "DuplicateKey")));
        Assert.True(CommandErrorsMetricProvider.IsRetryable(CommandError(189, "PrimarySteppedDown")));
        Assert.True(CommandErrorsMetricProvider.IsRetryable(new MongoConnectionException(Connection, "reset")));
        Assert.Equal(string.Empty, CommandErrorsMetricProvider.CodeName(new TimeoutException()));
        Assert.False(CommandErrorsMetricProvider.IsRetryable(new TimeoutException()));
    }

    [Fact]
    public void Failure_followed_by_the_same_command_on_the_operation_is_a_retry()
    {
        const string collection = "replay_retry";
        var replay = new EventReplay(Database);
        var retry = replay.Provider<CommandRetryMetricProvider>();
        var operationId = NextOperationId();
        var insert = new BsonDocument { { "insert", collection }, { "documents", new BsonArray { new BsonDocument("n", 1) } } };

        replay.Fail("insert", insert, new MongoConnectionException(Connection, "reset"), operationId);
        replay.Succeed("insert", insert, new BsonDocument { { "ok", 1 }, { "n", 1 } }, operationId);

        Assert.Equal(1, retry.Retries.WithLabels("insert", collection, Database).Value);
        Assert.False(retry.IsPending(operationId));
    }

    [Fact]
    public void Different_command_or_operation_is_not_a_retry()
    {
        const string collection = "replay_not_retry";
        var replay = new EventReplay(Database);
        var retry = replay.Provider<CommandRetryMetricProvider>();
        var operationId = NextOperationId();
        var find = new BsonDocument("find", collection);

        replay.Fail("find", find, new MongoConnectionException(Connection, "reset"), operationId);
        replay.Succeed("count", new BsonDocument("count", collection), new BsonDocument { { "ok", 1 }, { "n", 0 } }, operationId);
        replay.Succeed("find", find, replay.CursorReply("firstBatch", collection, 0, 0), NextOperationId());

        Assert.Equal(0, retry.Retries.WithLabels("find", collection, Database).Value);
        Assert.Equal(0, retry.Retries.WithLabels("count", collection, Database).Value);
        Assert.True(retry.IsPending(operationId));
    }

    [Fact]
    public void Failure_never_retried_expires()
    {
        var replay = new EventReplay(Database);
        var retry = replay.Provider<CommandRetryMetricProvider>();
        var operationId = NextOperationId();
        replay.Fail("find", new BsonDocument("find", "replay_expired"), new MongoConnectionException(Connection, "reset"), operationId);

        Assert.True(retry.AgeForTests(operationId, retry.RetryWindow + TimeSpan.FromSeconds(1)));
        retry.Sweep(Stopwatch.GetTimestamp());

        Assert.False(retry.IsPending(operationId));
    }

    [Fact]
    public void Command_whose_end_never_arrives_is_swept_as_orphaned()
    {
        const string collection = "replay_orphan";
        var replay = new EventReplay(Database);
        var retry = replay.Provider<CommandRetryMetricProvider>();
        var operationId = NextOperationId();
        var orphan = replay.Start("count", new BsonDocument("count", collection), operationId);
        var live = replay.Start("count", new BsonDocument("count", collection), operationId);

        Assert.True(replay.Age(orphan, replay.Session.OrphanTimeout + TimeSpan.FromSeconds(1)));
        replay.Session.SweepOrphanedCommands(Stopwatch.GetTimestamp());

        Assert.Equal(1, replay.Session.OrphanedCommands);
        Assert.Equal(1, replay.Session.InFlightCommands);

        // The orphan's late end event is ignored; the fresh command is still measured.
        var reply = new BsonDocument { { "ok", 1 }, { "n", 0 } };
        replay.Complete("count", orphan, reply, operationId);
        replay.Complete("count", live, reply, operationId);
        Assert.Equal(1, replay.Provider<CommandDurationMetricProvider>().CommandDuration.WithLabels("count", "success", collection, Database).Count);
        Assert.Equal(0, replay.Session.InFlightCommands);
    }

    [Fact]
    public async Task Retried_write_is_counted_with_its_retryable_error()
    {
        const string collection = "e2e_retry_write";
        await using var fake = await FakeMongo.StartAsync(Database, new Mongo.Fakes.Server.MongoFakeServerOptions { ReplicaSetName = "rs0" });
        var coll = fake.Collection(collection);
        await fake.FailPointAsync(new BsonDocument
        {
            { "failCommands", new BsonArray { "insert" } },
            { "errorCode", 91 },
            { "errorLabels", new BsonArray { "RetryableWriteError" } },
        });

        await coll.InsertOneAsync(new BsonDocument("n", 1));

        Assert.Equal(1, fake.Provider<CommandRetryMetricProvider>().Retries.WithLabels("insert", collection, Database).Value);
        Assert.Equal(1, await coll.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        var exposition = await fake.Session.ExpositionAsync();
        Assert.Contains(
            $"code_name=\"ShutdownInProgress\",category=\"server\",retryable=\"true\",target_collection=\"{collection}\",target_db=\"{Database}\"}} 1",
            exposition);
    }

    [Fact]
    public async Task Retried_read_after_a_network_error_is_counted()
    {
        const string collection = "e2e_retry_read";
        await using var fake = await FakeMongo.StartAsync(Database);
        var coll = await fake.SeedAsync(collection, documents: 1);
        await fake.FailPointAsync(new BsonDocument
        {
            { "failCommands", new BsonArray { "find" } },
            { "closeConnection", true },
        });

        Assert.Single(await coll.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync());

        Assert.Equal(1, fake.Provider<CommandRetryMetricProvider>().Retries.WithLabels("find", collection, Database).Value);
        var exposition = await fake.Session.ExpositionAsync();
        Assert.Contains($"category=\"network\",retryable=\"true\",target_collection=\"{collection}\",target_db=\"{Database}\"}} 1", exposition);
    }

    private static MongoCommandException CommandError(int code, string codeName) =>
        new(Connection, "failed", new BsonDocument("find", "c"), new BsonDocument { { "ok", 0 }, { "code", code }, { "codeName", codeName }, { "errmsg", "failed" } });
}
