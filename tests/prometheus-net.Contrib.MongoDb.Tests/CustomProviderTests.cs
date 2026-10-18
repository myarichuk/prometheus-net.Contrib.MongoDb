using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;
using PrometheusNet.MongoDb.Events;
using PrometheusNet.MongoDb.Handlers;

namespace PrometheusNet.MongoDb.Tests;

public class CustomProviderTests
{
    [Fact]
    public async Task Custom_provider_receives_its_clients_events_only()
    {
        var provider = new RecordingStartProvider();
        await using var instrumented = await FakeMongo.StartAsync("custom", configureInstrumentation: o => o.Providers.Add(provider));
        await using var other = await FakeMongo.StartAsync("custom");

        await instrumented.Collection("c").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
        await other.Collection("c").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();

        Assert.Equal(new[] { "find" }, provider.Received.ToArray());
        Assert.Same(provider, instrumented.Provider<RecordingStartProvider>());
    }

    [Fact]
    public void Only_overridden_handlers_are_subscribed()
    {
        Assert.Equal(new[] { typeof(MongoCommandEventStart) }, ProviderWiring.HandledEventTypes(typeof(RecordingStartProvider)));
        Assert.Empty(ProviderWiring.HandledEventTypes(typeof(NoOpProvider)));
    }

    [Fact]
    public async Task Throwing_provider_is_counted_and_does_not_break_the_others()
    {
        var recording = new RecordingStartProvider();
        var replay = new EventReplay(session: TestMetrics.NewSession(o =>
        {
            o.Providers.Add(new ThrowingProvider());
            o.Providers.Add(recording);
        }));

        replay.Succeed("count", new BsonDocument("count", "c"), new BsonDocument { { "ok", 1 }, { "n", 0 } }, operationId: 1);

        Assert.Equal(new[] { "count" }, recording.Received.ToArray());
        Assert.Contains("mongodb_client_instrumentation_handler_errors_total{provider=\"ThrowingProvider\"} 1", await replay.Session.ExpositionAsync());
    }

    private sealed class NoOpProvider : IMongoDbClientMetricProvider
    {
    }

    private sealed class RecordingStartProvider : IMongoDbClientMetricProvider
    {
        public readonly ConcurrentQueue<string> Received = new();

        public void Handle(MongoCommandEventStart e) => Received.Enqueue(e.OperationRawType);
    }

    private sealed class ThrowingProvider : IMongoDbClientMetricProvider
    {
        public void Handle(MongoCommandEventStart e) => throw new InvalidOperationException("boom");
    }
}
