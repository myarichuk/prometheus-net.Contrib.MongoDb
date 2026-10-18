using System.Net;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Events;
using MongoDB.Driver.Core.Servers;

namespace PrometheusNet.MongoDb.Tests;

/// <summary>
/// Pushes real driver events (<see cref="CommandStartedEvent"/>, <see cref="CommandSucceededEvent"/>,
/// <see cref="CommandFailedEvent"/>) through the instrumentation's actual entry points, so
/// every library event is built exactly the way production builds it. Commands and replies
/// are wire-encoded <see cref="RawBsonDocument"/>s, as the driver hands them over.
/// <para/>
/// Rule: tests of a command path must go through here, never build library events by hand.
/// Hand-built events can carry state production never produces (that is how a broken
/// killCursors path once passed its tests).
/// </summary>
internal sealed class EventReplay
{
    private static int _nextRequestId = 1_000_000;

    private static long _nextConnection = 1;

    private readonly ConnectionId _connection;

    private readonly DatabaseNamespace _database;

    /// <param name="database">The database commands run against.</param>
    /// <param name="session">The instrumentation to replay into; a fresh one on its own registry by default.</param>
    public EventReplay(string database = "db", InstrumentationSession? session = null)
    {
        Session = session ?? TestMetrics.NewSession();
        var server = new ServerId(new ClusterId(1), new DnsEndPoint("replay", 27017));
        _connection = new ConnectionId(server, Interlocked.Increment(ref _nextConnection));
        _database = new DatabaseNamespace(database);
    }

    public InstrumentationSession Session { get; }

    public T Provider<T>()
        where T : class, PrometheusNet.MongoDb.Handlers.IMongoDbClientMetricProvider =>
        Session.Provider<T>();

    /// <summary>Runs one command start followed by its success.</summary>
    public void Succeed(string commandName, BsonDocument command, BsonDocument reply, long operationId, TimeSpan? duration = null)
    {
        var requestId = Interlocked.Increment(ref _nextRequestId);
        Session.OnCommandStarted(
            new CommandStartedEvent(commandName, Raw(command), _database, operationId, requestId, _connection));
        Session.OnCommandSucceeded(
            new CommandSucceededEvent(commandName, Raw(reply), _database, operationId, requestId, _connection, duration ?? TimeSpan.FromMilliseconds(3)));
    }

    public void Fail(string commandName, BsonDocument command, Exception failure, long operationId)
    {
        var requestId = Interlocked.Increment(ref _nextRequestId);
        Session.OnCommandStarted(
            new CommandStartedEvent(commandName, Raw(command), _database, operationId, requestId, _connection));
        Session.OnCommandFailed(
            new CommandFailedEvent(commandName, _database, failure, operationId, requestId, _connection, TimeSpan.FromMilliseconds(3)));
    }

    /// <summary>Starts a command and leaves it in flight; returns its request id.</summary>
    public int Start(string commandName, BsonDocument command, long operationId)
    {
        var requestId = Interlocked.Increment(ref _nextRequestId);
        Session.OnCommandStarted(
            new CommandStartedEvent(commandName, Raw(command), _database, operationId, requestId, _connection));
        return requestId;
    }

    /// <summary>Completes a command started with <see cref="Start"/>.</summary>
    public void Complete(string commandName, int requestId, BsonDocument reply, long operationId) =>
        Session.OnCommandSucceeded(
            new CommandSucceededEvent(commandName, Raw(reply), _database, operationId, requestId, _connection, TimeSpan.FromMilliseconds(3)));

    /// <summary>Back-dates an in-flight command started with <see cref="Start"/>.</summary>
    public bool Age(int requestId, TimeSpan age) => Session.AgeCommandForTests(_connection, requestId, age);

    public void Find(string collection, long operationId, long cursorId, int documents) =>
        Succeed(
            "find",
            new BsonDocument { { "find", collection }, { "filter", new BsonDocument() }, { "$db", _database.DatabaseName } },
            CursorReply("firstBatch", collection, cursorId, documents),
            operationId);

    public void GetMore(string collection, long operationId, long cursorId, long replyCursorId, int documents) =>
        Succeed(
            "getMore",
            new BsonDocument { { "getMore", cursorId }, { "collection", collection }, { "$db", _database.DatabaseName } },
            CursorReply("nextBatch", collection, replyCursorId, documents),
            operationId);

    public void KillCursors(string collection, long operationId, params long[] cursorIds) =>
        Succeed(
            "killCursors",
            new BsonDocument
            {
                { "killCursors", collection },
                { "cursors", new BsonArray(cursorIds) },
                { "$db", _database.DatabaseName },
            },
            new BsonDocument
            {
                { "cursorsKilled", new BsonArray(cursorIds) },
                { "cursorsNotFound", new BsonArray() },
                { "cursorsAlive", new BsonArray() },
                { "cursorsUnknown", new BsonArray() },
                { "ok", 1.0 },
            },
            operationId);

    /// <summary>A server cursor reply, in the server's element order (batch first, then id).</summary>
    public BsonDocument CursorReply(string batchName, string collection, long cursorId, int documents) =>
        new()
        {
            {
                "cursor",
                new BsonDocument
                {
                    { batchName, new BsonArray(Enumerable.Range(0, documents).Select(i => new BsonDocument { { "_id", i }, { "payload", "xxxxxxxx" } })) },
                    { "id", cursorId },
                    { "ns", $"{_database.DatabaseName}.{collection}" },
                }
            },
            { "ok", 1.0 },
        };

    public static RawBsonDocument Raw(BsonDocument document) => new(document.ToBson());
}
