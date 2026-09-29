using BenchmarkDotNet.Attributes;
using Mongo.Fakes.Server;
using MongoDB.Bson;
using MongoDB.Driver;
using PrometheusNet.Contrib.MongoDb;

namespace PrometheusNet.MongoDb.Benchmarks;

/// <summary>
/// Measures the overhead of the Prometheus instrumentation against a plain driver client.
/// Every scenario runs twice: once with <c>Plain_</c> (no instrumentation) and once with
/// <c>Instrumented_</c> (full <c>InstrumentForPrometheus</c> pipeline). The delta between
/// each pair is the cost of this library. The backend is <see cref="MongoFakeServer"/>,
/// an in-process wire-protocol double, so results reflect client-side overhead only.
/// </summary>
[MemoryDiagnoser]
[MinColumn]
[MaxColumn]
public class MongoBenchmarks
{
    private const string Database = "benchdb";
    private const string Collection = "bench";

    private const int SeedDocuments = 1000;
    private const int BulkInsertCount = 100;

    private MongoFakeServer? _server;

    private IMongoCollection<BsonDocument>? _plain;
    private IMongoCollection<BsonDocument>? _instrumented;

    private FilterDefinition<BsonDocument>? _matchOneFilter;
    private FilterDefinition<BsonDocument>? _complexFilter;
    private FilterDefinition<BsonDocument>? _failingFilter;

    [GlobalSetup]
    public async Task Setup()
    {
        var fixtureDir = Path.Combine(Path.GetTempPath(), $"prometheus-mongo-bench-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);

        _server = new MongoFakeServer(new BsonFileBackend(fixtureDir), port: 0);
        await _server.StartAsync(default);

        _plain = new MongoClient(_server.ConnectionString)
            .GetDatabase(Database)
            .GetCollection<BsonDocument>(Collection);

        var instrumentedSettings = MongoClientSettings
            .FromConnectionString(_server.ConnectionString)
            .InstrumentForPrometheus();
        _instrumented = new MongoClient(instrumentedSettings)
            .GetDatabase(Database)
            .GetCollection<BsonDocument>(Collection);

        var seed = Enumerable.Range(0, SeedDocuments).Select(i => new BsonDocument
        {
            { "index", i },
            { "name", $"doc-{i}" },
            { "payload", new string('x', 200) },
        });
        await _plain.InsertManyAsync(seed);

        _matchOneFilter = Builders<BsonDocument>.Filter.Eq("name", "doc-42");
        _complexFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Gte("index", 100),
            Builders<BsonDocument>.Filter.Lt("index", 900),
            Builders<BsonDocument>.Filter.In("name", Enumerable.Range(0, 50).Select(i => $"doc-{i}")));
        _failingFilter = new BsonDocument("name", new BsonDocument("$bogusOp", 1));
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    [Benchmark]
    public async Task Plain_InsertOne() =>
        await _plain!.InsertOneAsync(new BsonDocument { { "name", "fresh" } });

    [Benchmark]
    public async Task Instrumented_InsertOne() =>
        await _instrumented!.InsertOneAsync(new BsonDocument { { "name", "fresh" } });

    [Benchmark]
    public async Task Plain_InsertMany()
    {
        var docs = Enumerable.Range(0, BulkInsertCount)
            .Select(i => new BsonDocument { { "n", i } });
        await _plain!.InsertManyAsync(docs);
    }

    [Benchmark]
    public async Task Instrumented_InsertMany()
    {
        var docs = Enumerable.Range(0, BulkInsertCount)
            .Select(i => new BsonDocument { { "n", i } });
        await _instrumented!.InsertManyAsync(docs);
    }

    [Benchmark]
    public async Task<int> Plain_FindOne() =>
        (await (await _plain!.FindAsync(_matchOneFilter)).ToListAsync()).Count;

    [Benchmark]
    public async Task<int> Instrumented_FindOne() =>
        (await (await _instrumented!.FindAsync(_matchOneFilter)).ToListAsync()).Count;

    [Benchmark]
    public async Task<int> Plain_FindBulk() =>
        (await (await _plain!.FindAsync(FilterDefinition<BsonDocument>.Empty)).ToListAsync()).Count;

    [Benchmark]
    public async Task<int> Instrumented_FindBulk() =>
        (await (await _instrumented!.FindAsync(FilterDefinition<BsonDocument>.Empty)).ToListAsync()).Count;

    [Benchmark]
    public async Task<int> Plain_FindFiltered() =>
        (await (await _plain!.FindAsync(_complexFilter)).ToListAsync()).Count;

    [Benchmark]
    public async Task<int> Instrumented_FindFiltered() =>
        (await (await _instrumented!.FindAsync(_complexFilter)).ToListAsync()).Count;

    [Benchmark]
    public async Task Plain_FailingFind()
    {
        try
        {
            await (await _plain!.FindAsync(_failingFilter)).ToListAsync();
        }
        catch (MongoCommandException)
        {
        }
    }

    [Benchmark]
    public async Task Instrumented_FailingFind()
    {
        try
        {
            await (await _instrumented!.FindAsync(_failingFilter)).ToListAsync();
        }
        catch (MongoCommandException)
        {
        }
    }
}
