using MongoDB.Driver;
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
            var filter = filterBuilder.Ne(x => x.Id, "1");

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

    private static async Task TestMongoOperation(string operationType, Func<IMongoCollection<TestDocument>, Task> operation)
    {
        await MongoTestContext.RunAsync(async (collection, session) =>
        {
            await operation(collection);

            var duration = session.Provider<CommandDurationMetricProvider>().CommandDuration
                .WithLabels(operationType, "success", MongoTestContext.Collection, MongoTestContext.Database);
            Assert.True(duration.Count >= 1);
            Assert.True(duration.Sum > 0);
        });
    }
}
