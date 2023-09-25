using MongoDB.Driver;
using PrometheusNet.Contrib.MongoDb.Handlers;
using Xunit.Abstractions;

namespace PrometheusNet.MongoDb.Tests;

[Collection("NonConcurrentCollection")]
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
        if (!MetricProviderRegistrar.TryGetProvider<ConnectionMetricsProvider>(out var provider) || provider == null)
        {
            throw new Exception($"Failed to fetch an instance of {nameof(ConnectionMetricsProvider)}");
        }

        double initialCreationCount = 0;
        double initialClosureCount = 0;

        var endpoint = string.Empty;

        await MongoTestContext.RunAsync(async (collection, ctx) =>
        {
            endpoint = ctx.ConnectionString.Replace("mongodb://", string.Empty);

            initialCreationCount = provider.ConnectionCreationRate.WithLabels("1", endpoint).Value;
            initialClosureCount = provider.ConnectionDuration.WithLabels("1", endpoint).Count;

            await collection.InsertOneAsync(new TestDocument { Id = "1", Name = "Test1" });

            _ = collection.Find(x => x.Id == "2").ToList();
        });

        await Task.Delay(500); // allow MongoDb driver to shut down stuff properly and fire events

        var updatedCreationCount = provider.ConnectionCreationRate.WithLabels("1", endpoint).Value;
        var updatedClosureCount = provider.ConnectionDuration.WithLabels("1", endpoint).Count;

        Assert.True(updatedCreationCount > initialCreationCount);
        Assert.True(updatedClosureCount > initialClosureCount);
    }

}
