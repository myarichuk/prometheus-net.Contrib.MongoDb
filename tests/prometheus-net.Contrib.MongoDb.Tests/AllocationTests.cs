using System.Net;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Events;
using MongoDB.Driver.Core.Servers;

namespace PrometheusNet.MongoDb.Tests;

/// <summary>
/// Guards the hot path's allocations. The driver hands commands and replies over as wire
/// documents, whose accessors build a reader per lookup (hundreds of bytes each), so a stray
/// <c>TryGetValue</c> on the per-command path shows up here.
/// </summary>
public class AllocationTests
{
    private const int Commands = 2000;

    private static readonly ConnectionId Connection = new(new ServerId(new ClusterId(1), new DnsEndPoint("alloc", 27017)), 1);

    private static readonly DatabaseNamespace Database = new("allocdb");

    private static readonly BsonDocument CountReply = new() { { "ok", 1 }, { "n", 0 } };

    [Theory]
    [MemberData(nameof(CommandShapes))]
    public void A_command_allocates_little_beyond_its_driver_documents(string commandName, BsonDocument command, BsonDocument reply)
    {
        var commandBytes = command.ToBson();
        var replyBytes = reply.ToBson();
        var session = TestMetrics.NewSession();
        var requestId = 0;

        var documentsOnly = BytesPerCommand(commandBytes, replyBytes, (_, _) => { });
        var instrumented = BytesPerCommand(commandBytes, replyBytes, (c, r) =>
        {
            var id = ++requestId;
            session.OnCommandStarted(new CommandStartedEvent(commandName, c, Database, 7, id, Connection));
            session.OnCommandSucceeded(new CommandSucceededEvent(commandName, r, Database, 7, id, Connection, TimeSpan.FromMilliseconds(1)));
        });

        // What remains is the correlation map's entry between start and end.
        Assert.InRange(instrumented - documentsOnly, 0, 256);
    }

    public static TheoryData<string, BsonDocument, BsonDocument> CommandShapes() => new()
    {
        {
            "find",
            new BsonDocument { { "find", "orders" }, { "filter", new BsonDocument("name", "x") }, { "$db", "allocdb" } },
            new BsonDocument { { "cursor", new BsonDocument { { "firstBatch", new BsonArray { new BsonDocument("_id", 1) } }, { "id", 0L }, { "ns", "allocdb.orders" } } }, { "ok", 1.0 } }
        },
        {
            "aggregate",
            new BsonDocument { { "aggregate", "orders" }, { "pipeline", new BsonArray { new BsonDocument("$match", new BsonDocument()) } }, { "cursor", new BsonDocument() } },
            new BsonDocument { { "cursor", new BsonDocument { { "firstBatch", new BsonArray() }, { "id", 0L }, { "ns", "allocdb.orders" } } }, { "ok", 1.0 } }
        },
        {
            "getMore",
            new BsonDocument { { "getMore", 123L }, { "collection", "orders" }, { "$db", "allocdb" } },
            new BsonDocument { { "cursor", new BsonDocument { { "nextBatch", new BsonArray() }, { "id", 0L }, { "ns", "allocdb.orders" } } }, { "ok", 1.0 } }
        },
        { "count", new BsonDocument("count", "orders"), CountReply },
    };

    // Fresh wire documents per command, disposed afterwards, as the driver does.
    private static long BytesPerCommand(byte[] command, byte[] reply, Action<RawBsonDocument, RawBsonDocument> run)
    {
        for (var i = 0; i < 200; i++)
        {
            Run();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Commands; i++)
        {
            Run();
        }

        return (GC.GetAllocatedBytesForCurrentThread() - before) / Commands;

        void Run()
        {
            using var c = new RawBsonDocument(command);
            using var r = new RawBsonDocument(reply);
            run(c, r);
        }
    }
}
