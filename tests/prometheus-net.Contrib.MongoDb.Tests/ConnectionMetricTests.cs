using EphemeralMongo;
using MongoDB.Driver;
using PrometheusNet.Contrib.MongoDb.Handlers;
using PrometheusNet.MongoDb;
using Xunit.Abstractions;

namespace PrometheusNet.MongoDb.Tests
{
    public class ConnectionMetricsTests
    {
        private readonly ITestOutputHelper _output;

        public ConnectionMetricsTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task TestConnectionCreationAndClosure()
        {
            using var ephemeralMongo = MongoRunner.Run();
            var endpoint = ephemeralMongo.ConnectionString.Replace("mongodb://", string.Empty);
            TestConnectionOperation(endpoint, () =>
            {
                MongoClient client;
                var settings =
                    MongoClientSettings
                        .FromConnectionString(ephemeralMongo.ConnectionString)
                        .InstrumentForPrometheus();

                client = new MongoClient(settings);
                var database = client.GetDatabase("test");
                var collection = database.GetCollection<TestDocument>("test123");
                _ = collection.Find(x => x.Id == "1").ToList();

                ephemeralMongo.Dispose();
            });
        }

        private void TestConnectionOperation(string endpoint, Action operation)
        {
            _output.WriteLine($"Starting test with endpoint: {endpoint}");

            MetricProviderRegistrar.RegisterAll();

            if (!MetricProviderRegistrar.TryGetProvider<ConnectionMetricsProvider>(out var provider) || provider == null)
            {
                throw new Exception($"Failed to fetch an instance of {nameof(ConnectionMetricsProvider)}");
            }

            var initialCreationCount = provider.ConnectionCreationRate.WithLabels("1", endpoint).Value;
            var initialClosureCount = provider.ConnectionDuration.WithLabels("1", endpoint).Count;

            _output.WriteLine($"Initial creation count: {initialCreationCount}, Initial closure count: {initialClosureCount}");

            operation();

            var updatedCreationCount = provider.ConnectionCreationRate.WithLabels("1", endpoint).Value;
            var updatedClosureCount = provider.ConnectionDuration.WithLabels("1", endpoint).Count;

            _output.WriteLine($"Updated creation count: {updatedCreationCount}, Updated closure count: {updatedClosureCount}");

            Assert.True(updatedCreationCount > initialCreationCount);
            Assert.True(updatedClosureCount > initialClosureCount);
        }
    }
}
