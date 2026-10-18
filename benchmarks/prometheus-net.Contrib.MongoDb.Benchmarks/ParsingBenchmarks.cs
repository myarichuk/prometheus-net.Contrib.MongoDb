using BenchmarkDotNet.Attributes;
using MongoDB.Bson;
using PrometheusNet.Contrib.MongoDb;
using PrometheusNet.MongoDb.Events;

namespace PrometheusNet.MongoDb.Benchmarks;

/// <summary>
/// Per-phase costs of the instrumentation, isolated from the driver and the network.
/// The scaling series over <see cref="Documents"/> is the point: reply inspection must not
/// grow with batch size. <c>Legacy_</c> methods are the accessor-based paths that the raw
/// walkers replaced (and still fall back to).
/// </summary>
[MemoryDiagnoser]
public class ParsingBenchmarks
{
    private readonly MongoCommandEventSuccess _target = new();

    private RawBsonDocument _reply = null!;

    private BsonDocument _insertCommand = null!;

    [Params(10, 100, 1_000, 10_000)]
    public int Documents { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var batch = new BsonArray(Enumerable.Range(0, Documents).Select(i => new BsonDocument
        {
            { "_id", i },
            { "name", $"doc-{i}" },
            { "payload", new string('x', 100) },
        }));

        // Server element order: the batch precedes the id.
        _reply = new RawBsonDocument(new BsonDocument
        {
            { "cursor", new BsonDocument { { "firstBatch", batch }, { "id", 12345L }, { "ns", "db.coll" } } },
            { "ok", 1.0 },
        }.ToBson());

        // Shape of a driver insert: materialized command, wire-encoded payload documents.
        _insertCommand = new BsonDocument
        {
            { "insert", "coll" },
            { "ordered", true },
            { "$db", "db" },
            { "documents", new BsonArray(batch.Select(d => (BsonValue)new RawBsonDocument(d.AsBsonDocument.ToBson()))) },
        };
    }

    [Benchmark]
    public int? CursorInfo()
    {
        _target.Reset();
        MongoInstrumentation.ExtractCursorInfo(_reply, _target);
        return _target.BatchDocumentCount;
    }

    [Benchmark]
    public int? Legacy_CursorInfo()
    {
        _target.Reset();
        MongoInstrumentation.ExtractCursorInfoSlow(_reply, _target);
        return _target.BatchDocumentCount;
    }

    [Benchmark]
    public int ReplySize() => BsonSizeCounter.GetSizeInBytes(_reply);

    [Benchmark]
    public int Legacy_ReplySize() => BsonSizeCounter.GetSizeBySerializing(_reply);

    [Benchmark]
    public int InsertCommandSize() => BsonSizeCounter.GetSizeInBytes(_insertCommand);

    [Benchmark]
    public int Legacy_InsertCommandSize() => BsonSizeCounter.GetSizeBySerializing(_insertCommand);
}
