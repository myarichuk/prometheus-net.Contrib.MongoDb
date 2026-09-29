using Mongo.Fakes.Server;
using MongoDB.Driver;
using PrometheusNet.Contrib.MongoDb;
using Xunit.Abstractions;

namespace PrometheusNet.MongoDb.Tests;

    /// <summary>
    /// Provides utility methods for MongoDB test execution.
    /// Tests run against <see cref="MongoFakeServer"/>, an in-process wire-protocol
    /// double: no <c>mongod</c> binary is needed. We only assert what this library
    /// observes (driver events turned into metrics), so a real server would add
    /// nothing but download time and flakes.
    /// </summary>
    internal static class MongoTestContext
    {
        /// <summary>
        /// Database name used in the test context
        /// </summary>
        public const string Database = "test";

        /// <summary>
        /// Collection name used in the test context
        /// </summary>
        public const string Collection = "testCollection";

        /// <summary>
        /// Executes a MongoDB operation within a test context.
        /// </summary>
        /// <param name="operation">The MongoDB operation to execute.</param>
        /// <param name="outputHelper">Optional logging helper for test output.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        public static async Task RunAsync(Func<IMongoCollection<TestDocument>, Task> operation, ITestOutputHelper? outputHelper = null)
        {
            var fixtureDir = CreateFixtureDir();
            try
            {
                await using var server = await StartServerAsync(fixtureDir);

                var settings = MongoClientSettings
                                    .FromConnectionString(server.ConnectionString)
                                    .InstrumentForPrometheus(); // wiring up the metrics

                var client = new MongoClient(settings);

                var database = client.GetDatabase(Database);
                var collection = database.GetCollection<TestDocument>(Collection);

                await operation(collection);
            }
            finally
            {
                DeleteFixtureDir(fixtureDir);
            }
        }

        /// <summary>
        /// Executes a MongoDB operation within a test context.
        /// </summary>
        /// <param name="operation">The MongoDB operation to execute.</param>
        /// <param name="outputHelper">Optional logging helper for test output.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        public static async Task RunAsync(Func<IMongoCollection<TestDocument>, Context, Task> operation, ITestOutputHelper? outputHelper = null)
        {
            var fixtureDir = CreateFixtureDir();
            try
            {
                await using var server = await StartServerAsync(fixtureDir);

                var settings = MongoClientSettings
                                    .FromConnectionString(server.ConnectionString)
                                    .InstrumentForPrometheus(); // wiring up the metrics

                var client = new MongoClient(settings);

                var database = client.GetDatabase("test");
                var collection = database.GetCollection<TestDocument>("testCollection");

                await operation(collection, new Context { ConnectionString = server.ConnectionString });
            }
            finally
            {
                DeleteFixtureDir(fixtureDir);
            }
        }

        private static string CreateFixtureDir()
        {
            var fixtureDir = Path.Combine(Path.GetTempPath(), $"prometheus-mongo-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(fixtureDir);
            return fixtureDir;
        }

        private static async Task<MongoFakeServer> StartServerAsync(string fixtureDir)
        {
            var server = new MongoFakeServer(new BsonFileBackend(fixtureDir), port: 0);
            await server.StartAsync(default);
            return server;
        }

        private static void DeleteFixtureDir(string fixtureDir)
        {
            try
            {
                Directory.Delete(fixtureDir, recursive: true);
            }
            catch (IOException)
            {
                // Best effort cleanup of the temp fixture folder.
            }
        }

        /// <summary>
        /// A test context for MongoDB operation.
        /// </summary>
        public class Context
        {
            /// <summary>
            /// Gets or sets the connection string for the MongoDB test context.
            /// </summary>
            public string ConnectionString { get; init; } = string.Empty;
        }
    }
