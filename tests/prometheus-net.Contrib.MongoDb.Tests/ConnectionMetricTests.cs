using MongoDB.Driver;
using PrometheusNet.Contrib.MongoDb.Handlers;
using Xunit.Abstractions;

namespace PrometheusNet.MongoDb.Tests;

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

        await MongoTestContext.RunAsync(
            async (collection, ctx) =>
            {
                endpoint = ctx.ConnectionString.Replace("mongodb://", string.Empty);

                initialCreationCount = provider.ConnectionCreationRate.WithLabels("1", endpoint).Value;
                initialClosureCount = provider.ConnectionDuration.WithLabels("1", endpoint).Count;

                await collection.InsertOneAsync(new TestDocument { Id = "1", Name = "Test1" });

                _ = collection.Find(x => x.Id == "2").ToList();
            },
            outputHelper: _output);

        int retryCount = 0;
        const int maxRetries = 5;

        double updatedCreationCount = 0;
        double updatedClosureCount = 0;

        while (retryCount < maxRetries)
        {
            await Task.Delay(250);

            updatedCreationCount = provider.ConnectionCreationRate.WithLabels("1", endpoint).Value;
            updatedClosureCount = provider.ConnectionDuration.WithLabels("1", endpoint).Count;

            if (updatedCreationCount > initialCreationCount && updatedClosureCount > initialClosureCount)
            {
                break;
            }

            retryCount++;
        }

        Assert.True(updatedCreationCount > initialCreationCount);
        Assert.True(updatedClosureCount > initialClosureCount);
    }

}
