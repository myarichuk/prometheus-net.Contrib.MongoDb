using MongoDB.Driver;
using PrometheusNet.Contrib.MongoDb;

namespace PrometheusNet.MongoDb.Tests;

/// <summary>
/// Runs typed-collection operations against <see cref="Mongo.Fakes.Server.MongoFakeServer"/>,
/// an in-process wire-protocol double: no <c>mongod</c> binary is needed. We only assert
/// what this library observes (driver events turned into metrics).
/// </summary>
internal static class MongoTestContext
{
    /// <summary>Database name used in the test context.</summary>
    public const string Database = "test";

    /// <summary>Collection name used in the test context.</summary>
    public const string Collection = "testCollection";

    /// <summary>
    /// Runs <paramref name="operation"/> on a fresh server, with a client instrumented on a
    /// registry of its own.
    /// </summary>
    public static async Task RunAsync(
        Func<IMongoCollection<TestDocument>, InstrumentationSession, Task> operation,
        Action<MongoInstrumentationOptions>? configure = null)
    {
        await using var fake = await FakeMongo.StartAsync(Database, configureInstrumentation: configure);
        var collection = fake.Client.GetDatabase(Database).GetCollection<TestDocument>(Collection);
        await operation(collection, fake.Session);
    }
}
