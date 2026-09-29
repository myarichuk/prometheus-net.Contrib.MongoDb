using MongoDB.Bson;
using MongoDB.Driver;
using Mongo.Fakes.Server;
using PrometheusNet.Contrib.MongoDb;
using PrometheusNet.Contrib.MongoDb.Handlers;
using PrometheusNet.MongoDb.Handlers;

namespace PrometheusNet.MongoDb.Tests;

/// <summary>
/// Functional tests against <see cref="MongoFakeServer"/> (in-process wire-protocol
/// double): fast, no <c>mongod</c> binary download, deterministic single-batch replies.
/// Each test uses a dedicated collection so its metric label combination is fresh and
/// the assertions can be absolute instead of before/after deltas.
/// </summary>
public class FakeServerInstrumentationTests : IAsyncLifetime
{
    private const string Database = "fakesdb";

    private MongoFakeServer? _server;

    private IMongoClient? _client;

    private string _fixtureDir = string.Empty;

    public async Task InitializeAsync()
    {
        _fixtureDir = Path.Combine(Path.GetTempPath(), $"prometheus-mongo-fakes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_fixtureDir);

        _server = new MongoFakeServer(new BsonFileBackend(_fixtureDir), port: 0);
        await _server.StartAsync(default);

        var settings = MongoClientSettings
            .FromConnectionString(_server.ConnectionString)
            .InstrumentForPrometheus();

        _client = new MongoClient(settings);
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        try
        {
            Directory.Delete(_fixtureDir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup of the temp fixture folder.
        }
    }

    [Fact]
    public async Task Insert_records_duration_and_request_response_sizes()
    {
        const string collection = "fake_insert_metrics";
        var coll = Collection(collection);

        if (!MetricProviderRegistrar.TryGetProvider<CommandDurationMetricProvider>(out var durationProvider) ||
            durationProvider is null ||
            !MetricProviderRegistrar.TryGetProvider<CommandRequestSizeMetricProvider>(out var requestSizeProvider) ||
            requestSizeProvider is null ||
            !MetricProviderRegistrar.TryGetProvider<CommandResponseSizeProvider>(out var responseSizeProvider) ||
            responseSizeProvider is null)
        {
            throw new Exception("Failed to fetch metric providers");
        }

        await coll.InsertOneAsync(new BsonDocument { { "name", "a" }, { "n", 1 } });

        Assert.Equal(
            1,
            durationProvider.CommandDurationHistogram
                .WithLabels("insert", "success", collection, Database).Count);
        Assert.Equal(
            1,
            CommandRequestSizeMetricProvider.CommandRequestSize
                .WithLabels("insert", collection, Database).Count);
        Assert.Equal(
            1,
            CommandResponseSizeProvider.CommandResponseSize
                .WithLabels("insert", collection, Database).Count);
    }

    [Fact]
    public async Task Find_records_query_count_filter_size_and_cursor_metrics()
    {
        const string collection = "fake_find_metrics";
        var coll = Collection(collection);

        if (!MetricProviderRegistrar.TryGetProvider<QueryCountMetricProvider>(out var queryCountProvider) ||
            queryCountProvider is null ||
            !MetricProviderRegistrar.TryGetProvider<QueryFilterSizeMetricProvider>(out var filterSizeProvider) ||
            filterSizeProvider is null ||
            !MetricProviderRegistrar.TryGetProvider<OpenCursorsMetricsProvider>(out var openCursorsProvider) ||
            openCursorsProvider is null ||
            !MetricProviderRegistrar.TryGetProvider<DocumentCountInCursorMetricProvider>(out var documentCountProvider) ||
            documentCountProvider is null)
        {
            throw new Exception("Failed to fetch metric providers");
        }

        await coll.InsertManyAsync(
        [
            new BsonDocument { { "name", "a" } },
            new BsonDocument { { "name", "a" } },
            new BsonDocument { { "name", "b" } },
        ]);

        var found = await coll.Find(Builders<BsonDocument>.Filter.Eq("name", "a")).ToListAsync();

        Assert.Equal(2, found.Count);
        Assert.Equal(
            1,
            queryCountProvider.QueryCount.WithLabels("find", collection, Database).Value);
        Assert.Equal(
            1, // { name: "a" } is a single leaf clause
            filterSizeProvider.QueryFilterSize.WithLabels("find", collection, Database).Sum);

        // Single-batch cursor: opened and closed within the same reply, gauge back to zero.
        Assert.Equal(
            0,
            openCursorsProvider.OpenCursors.WithLabels(collection, Database).Value);

        // Single batch of 2 documents observed exactly once.
        var exposition = await CollectExpositionAsync();
        Assert.Contains(
            $"mongodb_client_cursor_document_count_sum{{target_collection=\"{collection}\",target_db=\"{Database}\"}} 2",
            exposition);
    }

    [Fact]
    public async Task Failing_find_records_error_and_failure_duration()
    {
        const string collection = "fake_error_metrics";
        var coll = Collection(collection);

        if (!MetricProviderRegistrar.TryGetProvider<CommandErrorsMetricProvider>(out var errorsProvider) ||
            errorsProvider is null ||
            !MetricProviderRegistrar.TryGetProvider<CommandDurationMetricProvider>(out var durationProvider) ||
            durationProvider is null)
        {
            throw new Exception("Failed to fetch metric providers");
        }

        var failure = await Assert.ThrowsAsync<MongoCommandException>(
            () => coll.Find(new BsonDocument("name", new BsonDocument("$bogusOp", 1))).ToListAsync());

        Assert.NotNull(failure);

        var exposition = await CollectExpositionAsync();
        Assert.Contains(
            $"mongodb_client_command_errors_total_count{{command_type=\"find\",error_type=\"{nameof(MongoCommandException)}\",target_collection=\"{collection}\",target_db=\"{Database}\"}} 1",
            exposition);
        Assert.Equal(
            1,
            durationProvider.CommandDurationHistogram
                .WithLabels("find", "failure", collection, Database).Count);
    }

    private IMongoCollection<BsonDocument> Collection(string name) =>
        _client!.GetDatabase(Database).GetCollection<BsonDocument>(name);

    // Summary children expose no readable value API, so assert through exposition text.
    private static async Task<string> CollectExpositionAsync()
    {
        using var stream = new MemoryStream();
        await Prometheus.Metrics.DefaultRegistry.CollectAndExportAsTextAsync(stream, default);
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
