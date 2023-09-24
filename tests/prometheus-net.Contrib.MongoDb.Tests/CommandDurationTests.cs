using EphemeralMongo;
using MongoDB.Driver;
using Prometheus;
using PrometheusNet.MongoDb;
using PrometheusNet.MongoDb.Handlers;

namespace PrometheusNet.MongoDb.Tests;

public class CommandDurationTests
{
    [Fact]
    public async Task TestInsertOperation()
    {
        await TestMongoOperation("insert", async collection =>
        {
            await collection.InsertOneAsync(new TestDocument { Id = "1", Name = "Test" });
        });
    }

    [Fact]
    public async Task TestDeleteOperation()
    {
        await TestMongoOperation("delete", async collection =>
        {
            await collection.DeleteOneAsync(x => x.Id == "1");
        });
    }

    [Fact]
    public async Task TestFindOperation()
    {
        await TestMongoOperation("find", async collection =>
        {
            for (int i = 0; i < 300; i++)
            {
                await collection.InsertOneAsync(new TestDocument { Id = i.ToString(), Name = "Test1" });
            }

            var filterBuilder = Builders<TestDocument>.Filter;
            var filter = filterBuilder.And(
                filterBuilder.Ne(x => x.Id, "1"),
                filterBuilder.In(x => x.Name, new List<string> { "Test5", "Test6", "Test7" }));

            var findOptions = new FindOptions<TestDocument>
            {
                BatchSize = 100
            };

            var docs = (await collection.FindAsync(filter, findOptions)).ToList();
        });
    }

    [Fact]
    public async Task TestMongoDeleteMultipleDocuments()
    {
        await TestMongoOperation("delete", async collection =>
        {
            await collection.InsertManyAsync(new[]
            {
                new TestDocument { Id = "1", Name = "Test1" },
                new TestDocument { Id = "2", Name = "Test2" },
                new TestDocument { Id = "3", Name = "Test3" },
            });
            await collection.DeleteManyAsync(Builders<TestDocument>.Filter.Empty);
        });
    }

    private async Task TestMongoOperation(string operationType, Func<IMongoCollection<TestDocument>, Task> operation)
    {
        using var ephemeralMongo = MongoRunner.Run();

        var settings = MongoClientSettings
            .FromConnectionString(ephemeralMongo.ConnectionString)
            .InstrumentForPrometheus();

        var client = new MongoClient(settings);

        var database = client.GetDatabase("test");
        var collection = database.GetCollection<TestDocument>("testCollection");

        if (!MetricProviderRegistrar.TryGetProvider<CommandDurationProvider>(out var provider))
        {
            throw new Exception($"Failed to fetch an instance of {nameof(CommandDurationProvider)}");
        }

        // perform the operation and assert that the Prometheus metric is updated
        var initialCount = GetSampleValue(provider.CommandDuration, operationType, "success", "testCollection", "test");
        await operation(collection);
        var updatedCount = GetSampleValue(provider.CommandDuration, operationType, "success", "testCollection", "test");

        Assert.True(updatedCount > initialCount); // account for parallelism, so it won't be necessarily +1 difference
    }

    private double GetSampleValue(Histogram metric, string commandType, string status, string collectionName, string db)
    {
        return metric
            .WithLabels(commandType, status, collectionName, db)
            .Count;
    }
}

public class TestDocument
{
    public string Id { get; set; }

    public string Name { get; set; }
}
