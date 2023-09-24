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
        public static async Task ExecuteCommand(Func<IMongoCollection<TestDocument>, Task> operation, ITestOutputHelper? outputHelper = null)
        {
            // Initialize and run MongoDB instance for testing
            using var mongo = MongoRunner.Run(new MongoRunnerOptions
            {
                KillMongoProcessesWhenCurrentProcessExits = true,
                StandardErrorLogger = outputHelper != null ? outputHelper.WriteLine : _emptyLogger,
                StandardOuputLogger = outputHelper != null ? outputHelper.WriteLine : _emptyLogger,
            });

            // Connect to the MongoDB instance
            var client = new MongoClient(mongo.ConnectionString);

            // Get the database and collection
            var database = client.GetDatabase("test");
            var collection = database.GetCollection<TestDocument>("testCollection");

            // Execute the MongoDB operation
            await operation(collection);
        }
    }