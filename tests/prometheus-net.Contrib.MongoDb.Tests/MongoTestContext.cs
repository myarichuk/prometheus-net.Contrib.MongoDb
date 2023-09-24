using EphemeralMongo;
using MongoDB.Driver;
using Xunit.Abstractions;

namespace PrometheusNet.MongoDb.Tests;

    /// <summary>
    /// Provides utility methods for MongoDB test execution.
    /// </summary>
    internal static class MongoTestContext
    {
        /// <summary>
        /// An empty logger that does nothing.
        /// </summary>
        private static readonly Logger _emptyLogger = _ => { };

        /// <summary>
        /// Executes a MongoDB operation within a test context.
        /// </summary>
        /// <param name="operation">The MongoDB operation to execute.</param>
        /// <param name="outputHelper">Optional logging helper for test output.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        public static async Task RunAsync(Func<IMongoCollection<TestDocument>, Task> operation, ITestOutputHelper? outputHelper = null)
        {
            using var mongo = MongoRunner.Run(new MongoRunnerOptions
            {
                KillMongoProcessesWhenCurrentProcessExits = true,
                StandardErrorLogger = outputHelper != null ? outputHelper.WriteLine : _emptyLogger,
                StandardOuputLogger = outputHelper != null ? outputHelper.WriteLine : _emptyLogger,
            });

            var settings = MongoClientSettings
                                .FromConnectionString(mongo.ConnectionString)
                                .InstrumentForPrometheus(); // wiring up the metrics

            var client = new MongoClient(settings);

            var database = client.GetDatabase("test");
            var collection = database.GetCollection<TestDocument>("testCollection");

            await operation(collection);
        }

        /// <summary>
        /// Executes a MongoDB operation within a test context.
        /// </summary>
        /// <param name="operation">The MongoDB operation to execute.</param>
        /// <param name="outputHelper">Optional logging helper for test output.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        public static async Task RunAsync(Func<IMongoCollection<TestDocument>, Context, Task> operation, ITestOutputHelper? outputHelper = null)
        {
            using var mongo = MongoRunner.Run(new MongoRunnerOptions
            {
                KillMongoProcessesWhenCurrentProcessExits = true,
                StandardErrorLogger = outputHelper != null ? outputHelper.WriteLine : _emptyLogger,
                StandardOuputLogger = outputHelper != null ? outputHelper.WriteLine : _emptyLogger,
            });

            var settings = MongoClientSettings
                                .FromConnectionString(mongo.ConnectionString)
                                .InstrumentForPrometheus(); // wiring up the metrics

            var client = new MongoClient(settings);

            var database = client.GetDatabase("test");
            var collection = database.GetCollection<TestDocument>("testCollection");

            await operation(collection, new Context { ConnectionString = mongo.ConnectionString });
        }

        /// <summary>
        /// A test context for MongoDB operation.
        /// </summary>
        public class Context
        {
            /// <summary>
            /// Gets or sets the connection string for the MongoDB test context.
            /// </summary>
            public string ConnectionString { get; init; }
        }
    }