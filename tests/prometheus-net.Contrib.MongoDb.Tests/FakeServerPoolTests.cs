using System.Net;
using System.Net.Sockets;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Events;
using MongoDB.Driver.Core.Servers;
using PrometheusNet.Contrib.MongoDb;
using PrometheusNet.Contrib.MongoDb.Handlers;
using static PrometheusNet.MongoDb.Tests.TestMetrics;

namespace PrometheusNet.MongoDb.Tests;

/// <summary>
/// End-to-end connection pool, server selection and server monitoring tests against
/// <see cref="Mongo.Fakes.Server.MongoFakeServer"/>, each with a server and registry of its own.
/// </summary>
public class FakeServerPoolTests
{
    private const string Database = "pooldb";

    private FakeMongo? _fake;

    private ConnectionPoolMetricsProvider Pool => _fake!.Provider<ConnectionPoolMetricsProvider>();

    private ClusterMetricsProvider Cluster => _fake!.Provider<ClusterMetricsProvider>();

    [Fact]
    public async Task Checkouts_are_timed_and_connections_are_returned()
    {
        await using var fake = await StartAsync(Database);
        var coll = await fake.SeedAsync("pool_checkout", documents: 3);

        await coll.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
        await coll.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);

        // Seed, find and count: at least three checkouts on this server.
        Assert.True(Pool.CheckoutDuration.WithLabels(fake.Server).Count >= 3);
        await WaitUntilAsync(() => Pool.ConnectionsInUse.WithLabels(fake.Server).Value == 0);
        Assert.True(Pool.Connections.WithLabels(fake.Server).Value >= 1);
    }

    [Fact]
    public async Task Exhausted_pool_records_a_checkout_timeout()
    {
        await using var fake = await StartAsync(Database, configureClient: settings =>
        {
            settings.MaxConnectionPoolSize = 1;
            settings.WaitQueueTimeout = TimeSpan.FromMilliseconds(200);
        });
        var coll = await fake.SeedAsync("pool_saturated", documents: 1);
        await fake.FailPointAsync(new BsonDocument
        {
            { "failCommands", new BsonArray { "find" } },
            { "blockConnection", true },
            { "blockTimeMS", 1000 },
        });

        // The blocked find holds the only connection; the count waits for it and gives up.
        var blocked = coll.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
        await WaitUntilAsync(() => Pool.ConnectionsInUse.WithLabels(fake.Server).Value == 1);
        await Assert.ThrowsAnyAsync<TimeoutException>(() => coll.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        await blocked;

        Assert.Equal(1, Pool.CheckoutFailures.WithLabels(fake.Server, ConnectionPoolMetricsProvider.Timeout).Value);
        await WaitUntilAsync(() => Pool.ConnectionsInUse.WithLabels(fake.Server).Value == 0);
    }

    [Fact]
    public async Task Network_error_clears_the_pool()
    {
        await using var fake = await StartAsync(Database);
        var coll = await fake.SeedAsync("pool_cleared", documents: 1);
        await fake.FailPointAsync(new BsonDocument
        {
            { "failCommands", new BsonArray { "find" } },
            { "closeConnection", true },
        });

        // The driver clears the pool on the network error, then retries the read.
        Assert.Single(await coll.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync());

        await WaitUntilAsync(() => Pool.Cleared.WithLabels(fake.Server).Value >= 1);
        await WaitUntilAsync(() => Pool.ConnectionsInUse.WithLabels(fake.Server).Value == 0);
    }

    [Fact]
    public async Task Server_discovery_records_the_type_change_and_round_trip_time()
    {
        await using var fake = await StartAsync(Database);
        await fake.Client.GetDatabase(Database).RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));

        await WaitUntilAsync(() => Cluster.TypeChanges.WithLabels(fake.Server, "standalone").Value == 1);
        await WaitUntilAsync(() => Cluster.RoundTripTime.WithLabels(fake.Server).Value > 0);
    }

    [Fact]
    public async Task Server_selection_is_timed_per_operation()
    {
        await using var fake = await StartAsync(Database);
        await fake.Collection("selection").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();

        Assert.Equal(1, Cluster.SelectionDuration.WithLabels("find").Count);
    }

    [Fact]
    public async Task Unreachable_server_records_a_selection_failure()
    {
        var settings = MongoClientSettings.FromConnectionString($"mongodb://127.0.0.1:{UnusedPort()}/?directConnection=true");
        settings.ServerSelectionTimeout = TimeSpan.FromMilliseconds(300);
        settings.ConnectTimeout = TimeSpan.FromMilliseconds(100);
        var cluster = MongoInstrumentation.Instrument(settings, o => o.Registry = Prometheus.Metrics.NewCustomRegistry()).Provider<ClusterMetricsProvider>();
        var client = new MongoClient(settings);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            client.GetDatabase(Database).GetCollection<BsonDocument>("unreachable").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync());

        Assert.Equal(1, cluster.SelectionFailures.WithLabels("find").Value);
        client.Dispose();
    }

    [Fact]
    public void Only_a_server_type_change_counts_as_a_topology_change()
    {
        var endPoint = new DnsEndPoint("type-change.test", 27017);
        var serverId = new ServerId(new ClusterId(), endPoint);
        ServerDescription Described(ServerType type, int rttMs) =>
            new(serverId, endPoint, type: type, state: ServerState.Connected, averageRoundTripTime: TimeSpan.FromMilliseconds(rttMs));
        var provider = new ClusterMetricsProvider(NewSession());
        var unknown = new ServerDescription(serverId, endPoint);

        provider.Handle(new ServerDescriptionChangedEvent(unknown, Described(ServerType.ReplicaSetPrimary, 5)));
        provider.Handle(new ServerDescriptionChangedEvent(Described(ServerType.ReplicaSetPrimary, 5), Described(ServerType.ReplicaSetPrimary, 9)));
        provider.Handle(new ServerDescriptionChangedEvent(Described(ServerType.ReplicaSetPrimary, 9), Described(ServerType.ReplicaSetSecondary, 9)));

        Assert.Equal(1, provider.TypeChanges.WithLabels("type-change.test:27017", "replica_set_primary").Value);
        Assert.Equal(1, provider.TypeChanges.WithLabels("type-change.test:27017", "replica_set_secondary").Value);
        Assert.Equal(0.009, provider.RoundTripTime.WithLabels("type-change.test:27017").Value, precision: 6);
    }

    [Fact]
    public void Server_labels_are_host_and_port()
    {
        Assert.Equal("db.example:27017", ServerLabel.Of(new DnsEndPoint("db.example", 27017)));
        Assert.Equal("127.0.0.1:27018", ServerLabel.Of(new IPEndPoint(IPAddress.Loopback, 27018)));
        Assert.Equal("[::1]:27019", ServerLabel.Of(new IPEndPoint(IPAddress.IPv6Loopback, 27019)));
        Assert.Equal(string.Empty, ServerLabel.Of(null));
    }

    private async Task<FakeMongo> StartAsync(string database, Action<MongoClientSettings>? configureClient = null) =>
        _fake = await FakeMongo.StartAsync(database, configureClient: configureClient);

    private static int UnusedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
