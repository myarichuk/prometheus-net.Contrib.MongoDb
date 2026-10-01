using System.Collections.Concurrent;
using PrometheusNet.MongoDb;
using PrometheusNet.MongoDb.Events;
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

    [Fact]
    public void Custom_provider_can_be_registered_and_receives_events()
    {
        var provider = new RecordingStartProvider();

        try
        {
            MetricProviderRegistrar.Register(provider);

            Assert.True(MetricProviderRegistrar.TryGetProvider<RecordingStartProvider>(out _));

            EventHub.Default.Publish(new MongoCommandEventStart { OperationRawType = "find" });

            Assert.Single(provider.Received);
        }
        finally
        {
            MetricProviderRegistrar.Unregister(provider);
        }

        EventHub.Default.Publish(new MongoCommandEventStart { OperationRawType = "find" });

        Assert.Single(provider.Received);
    }

    [Fact]
    public void Register_replaces_previous_provider_of_same_type()
    {
        var first = new RecordingStartProvider();
        var second = new RecordingStartProvider();

        try
        {
            MetricProviderRegistrar.Register(first);
            MetricProviderRegistrar.Register(second);

            EventHub.Default.Publish(new MongoCommandEventStart { OperationRawType = "find" });

            Assert.Empty(first.Received);
            Assert.Single(second.Received);
        }
        finally
        {
            MetricProviderRegistrar.Unregister(second);
        }
    }

    [Fact]
    public void RegisterAll_scans_custom_assemblies()
    {
        try
        {
            MetricProviderRegistrar.RegisterAll(new[] { typeof(MetricProviderRegistrarTests).Assembly });

            Assert.True(MetricProviderRegistrar.TryGetProvider<AssemblyScanProvider>(out _));
        }
        finally
        {
            if (MetricProviderRegistrar.TryGetProvider<AssemblyScanProvider>(out var scanProvider) && scanProvider is not null)
            {
                MetricProviderRegistrar.Unregister(scanProvider);
            }

            if (MetricProviderRegistrar.TryGetProvider<RecordingStartProvider>(out var recordingProvider) && recordingProvider is not null)
            {
                MetricProviderRegistrar.Unregister(recordingProvider);
            }

            if (MetricProviderRegistrar.TryGetProvider<TestMetricProvider>(out var testProvider) && testProvider is not null)
            {
                MetricProviderRegistrar.Unregister(testProvider);
            }
        }

        Assert.False(MetricProviderRegistrar.TryGetProvider<AssemblyScanProvider>(out _));
    }

    private sealed class TestMetricProvider : IMongoDbClientMetricProvider
    {
    }

    private sealed class RecordingStartProvider : IMongoDbClientMetricProvider
    {
        public readonly ConcurrentQueue<MongoCommandEventStart> Received = new();

        public void Handle(MongoCommandEventStart e) => Received.Enqueue(e);
    }

    private sealed class AssemblyScanProvider : IMongoDbClientMetricProvider
    {
        public void Handle(MongoCommandEventStart e)
        {
        }
    }
}
