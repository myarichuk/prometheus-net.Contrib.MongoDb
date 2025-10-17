using PrometheusNet.MongoDb;
using PrometheusNet.MongoDb.Handlers;
using Xunit;

namespace PrometheusNet.Contrib.MongoDb.Tests;

public class MetricProviderRegistrarTests
{
    [Fact]
    public void DoesNotRegisterProvidersFromOtherAssemblies()
    {
        var isRegistered = MetricProviderRegistrar.TryGetProvider<TestMetricProvider>(out var provider);

        Assert.False(isRegistered);
        Assert.Null(provider);
    }

    private sealed class TestMetricProvider : IMongoDbClientMetricProvider
    {
    }
}
