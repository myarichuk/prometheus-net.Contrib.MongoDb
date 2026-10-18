using MongoDB.Bson;
using MongoDB.Driver;
using PrometheusNet.Contrib.MongoDb.Handlers;
using static PrometheusNet.MongoDb.Tests.TestMetrics;

namespace PrometheusNet.MongoDb.Tests;

public class ConnectionMetricsTests
{
    [Fact]
    public async Task Connections_are_counted_and_their_lifetime_recorded_when_closed()
    {
        await using var fake = await FakeMongo.StartAsync("connections");
        var connections = fake.Provider<ConnectionMetricsProvider>();
        var coll = await fake.SeedAsync("connection_lifetime", documents: 1);
        Assert.True(connections.ConnectionsOpened.WithLabels(fake.Server).Value >= 1);
        Assert.Equal(0, connections.ConnectionLifetime.WithLabels(fake.Server).Count);

        // The server drops the connection mid-command; the driver closes it and retries the read.
        await fake.FailPointAsync(new BsonDocument
        {
            { "failCommands", new BsonArray { "find" } },
            { "closeConnection", true },
        });
        Assert.Single(await coll.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync());

        await WaitUntilAsync(() => connections.ConnectionLifetime.WithLabels(fake.Server).Count >= 1);
        Assert.True(connections.ConnectionsOpened.WithLabels(fake.Server).Value >= 2);
    }
}
