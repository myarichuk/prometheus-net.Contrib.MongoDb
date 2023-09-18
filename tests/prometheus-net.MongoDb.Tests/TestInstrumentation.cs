using EphemeralMongo;
using MongoDB.Driver;
using Prometheus;

public class MongoInstrumentationTests
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

    private async Task TestMongoOperation(string operationType, Func<IMongoCollection<TestDocument>, Task> operation)
    {
        using var ephemeralMongo = MongoRunner.Run();

        // Clear previous commands (useful for test setup)
        MongoInstrumentation.ClearCommands();

        var settings = MongoClientSettings
            .FromConnectionString(ephemeralMongo.ConnectionString)
            .InstrumentForPrometheus();

        var client = new MongoClient(settings);

        // Get a database and collection
        var database = client.GetDatabase("test");
        var collection = database.GetCollection<TestDocument>("testCollection");

        // Perform the operation and assert that the Prometheus metric is updated
        var initialCount = GetSampleValue(MongoInstrumentation.CommandDuration, operationType, "success");
        await operation(collection);
        var updatedCount = GetSampleValue(MongoInstrumentation.CommandDuration, operationType, "success");

        Assert.True(updatedCount > initialCount);
    }

    private double GetSampleValue(Histogram metric, string commandType, string status)
    {
        return metric
            .WithLabels(commandType, status)
            .Count;
    }
}

public class TestDocument
{
    public string Id { get; set; }
    public string Name { get; set; }
}
