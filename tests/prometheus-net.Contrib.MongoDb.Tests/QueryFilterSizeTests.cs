using EphemeralMongo;
using MongoDB.Driver;
using PrometheusNet.MongoDb.Handlers;

namespace PrometheusNet.MongoDb.Tests
{
    public class QueryFilterSizeTests
    {
        [Fact]
        public async Task TestSimpleFindOperation()
        {
            await TestMongoOperationWithFilterSize("find", Builders<TestDocument>.Filter.Eq(x => x.Id, "1"), 1);
        }

        [Fact]
        public async Task TestComplexFindOperation()
        {
            var filterBuilder = Builders<TestDocument>.Filter;
            var filter = filterBuilder.And(
                filterBuilder.Ne(x => x.Id, "1"),
                filterBuilder.In(x => x.Name, new List<string> { "Test5", "Test6", "Test7" })
            );
            await TestMongoOperationWithFilterSize("find", filter, 4); // Ne + In + List with 3 elements = 4
        }

        [Fact]
        public async Task TestNestedFindOperation()
        {
            var filterBuilder = Builders<TestDocument>.Filter;
            var filter = filterBuilder.Or(
                filterBuilder.And(
                    filterBuilder.Eq(x => x.Id, "1"),
                    filterBuilder.Ne(x => x.Name, "Test")
                ),
                filterBuilder.And(
                    filterBuilder.Eq(x => x.Id, "2"),
                    filterBuilder.Eq(x => x.Name, "Test")
                )
            );
            await TestMongoOperationWithFilterSize("find", filter, 6); // 2 ANDs with 2 children each + 1 OR = 6
        }

        private async Task TestMongoOperationWithFilterSize(string operationType, FilterDefinition<TestDocument> filter, int expectedFilterSize)
        {
            using var ephemeralMongo = MongoRunner.Run(new MongoRunnerOptions
            {
                KillMongoProcessesWhenCurrentProcessExits = true,
            });
            var settings = MongoClientSettings
                .FromConnectionString(ephemeralMongo.ConnectionString)
                .InstrumentForPrometheus();

            var client = new MongoClient(settings);

            var database = client.GetDatabase("test");
            var collection = database.GetCollection<TestDocument>("testCollection");

            // You would initialize QueryFilterSizeMetricProvider here and register it as needed
            // For example:
            var queryFilterSizeProvider = new QueryFilterSizeMetricProvider();

            // Then populate the collection with test data, if needed
            // e.g., await collection.InsertManyAsync(...);

            // Execute the MongoDB query
            await collection.FindAsync(filter, new FindOptions<TestDocument> { BatchSize = 100 });

            // Check the metric value
            // This assumes you have a way to directly access the metric or expose it for testing
            // e.g., double metricValue = queryFilterSizeProvider.QueryFilterSize.GetValue(...);
            // Assert.Equal(expectedFilterSize, metricValue);
        }
    }
}
