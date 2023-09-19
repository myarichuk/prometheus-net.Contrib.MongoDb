using EphemeralMongo;
using MongoDB.Driver;
using Prometheus;

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
            await collection.FindAsync(x => x.Id == "1");
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

        // perform the operation and assert that the Prometheus metric is updated
        var initialCount = GetSampleValue(MongoInstrumentation.CommandDuration, operationType, "success", "testCollection", "test");
        await operation(collection);
        var updatedCount = GetSampleValue(MongoInstrumentation.CommandDuration, operationType, "success", "testCollection", "test");

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
