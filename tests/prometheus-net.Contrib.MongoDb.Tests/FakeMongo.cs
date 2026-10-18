using MongoDB.Bson;
using MongoDB.Driver;
using Mongo.Fakes.Server;
using PrometheusNet.Contrib.MongoDb;

namespace PrometheusNet.MongoDb.Tests;

/// <summary>
/// A <see cref="MongoFakeServer"/> on its own port with an instrumented client. Each test
/// starts its own, so failpoints never leak between tests, and its client records on a
/// registry of its own, so every metric starts at zero.
/// </summary>
internal sealed class FakeMongo : IAsyncDisposable
{
    private readonly MongoFakeServer _server;

    private readonly string _fixtureDir;

    private FakeMongo(
        MongoFakeServer server,
        string fixtureDir,
        string database,
        Action<MongoClientSettings>? configureClient,
        Action<MongoInstrumentationOptions>? configureInstrumentation)
    {
        _server = server;
        _fixtureDir = fixtureDir;
        Database = database;
        var settings = MongoClientSettings.FromConnectionString(server.ConnectionString);
        configureClient?.Invoke(settings);
        Session = MongoInstrumentation.Instrument(settings, options =>
        {
            options.Registry = Prometheus.Metrics.NewCustomRegistry();
            configureInstrumentation?.Invoke(options);
        });
        Client = new MongoClient(settings);
    }

    public IMongoClient Client { get; }

    /// <summary>The client's instrumentation, on a registry of its own.</summary>
    public InstrumentationSession Session { get; }

    public string Database { get; }

    /// <summary>The <c>server</c> label this fake's metrics are recorded under.</summary>
    public string Server => $"127.0.0.1:{_server.Port}";

    public static async Task<FakeMongo> StartAsync(
        string database,
        MongoFakeServerOptions? options = null,
        Action<MongoClientSettings>? configureClient = null,
        Action<MongoInstrumentationOptions>? configureInstrumentation = null)
    {
        var fixtureDir = Path.Combine(Path.GetTempPath(), $"prometheus-mongo-fake-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var server = new MongoFakeServer(new BsonFileBackend(fixtureDir), options ?? new MongoFakeServerOptions());
        await server.StartAsync(default);
        return new FakeMongo(server, fixtureDir, database, configureClient, configureInstrumentation);
    }

    public T Provider<T>()
        where T : class, PrometheusNet.MongoDb.Handlers.IMongoDbClientMetricProvider =>
        Session.Provider<T>();

    public IMongoCollection<BsonDocument> Collection(string name) =>
        Client.GetDatabase(Database).GetCollection<BsonDocument>(name);

    public async Task<IMongoCollection<BsonDocument>> SeedAsync(string name, int documents)
    {
        var coll = Collection(name);
        await coll.InsertManyAsync(Enumerable.Range(0, documents).Select(i => new BsonDocument("n", i)));
        return coll;
    }

    public Task FailCommandAsync(string command, int errorCode) =>
        FailPointAsync(new BsonDocument { { "failCommands", new BsonArray { command } }, { "errorCode", errorCode } });

    /// <summary>Configures a one-shot <c>failCommand</c> failpoint with the given data.</summary>
    public Task FailPointAsync(BsonDocument data, int times = 1) =>
        Client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "configureFailPoint", "failCommand" },
            { "mode", new BsonDocument("times", times) },
            { "data", data },
        });

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _server.DisposeAsync();
        try
        {
            Directory.Delete(_fixtureDir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup of the temp fixture folder.
        }
    }
}
