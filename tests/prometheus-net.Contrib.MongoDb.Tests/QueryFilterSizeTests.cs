using MongoDB.Bson;
using MongoDB.Driver;
using Prometheus;
using PrometheusNet.MongoDb.Handlers;

namespace PrometheusNet.MongoDb.Tests
{
    [Collection("NonConcurrentCollection")]
    public class QueryFilterSizeTests
    {
        [Fact]
        public async Task TestSimpleFindOperation()
        {
            await TestMongoOperationWithFilterSizeAsync(Builders<TestDocument>.Filter.Eq(x => x.Id, "1"), 1);
        }

        [Fact]
        public async Task TestComplexFindOperation()
        {
            var filterBuilder = Builders<TestDocument>.Filter;
            var filter = filterBuilder.And(
                filterBuilder.Ne(x => x.Id, "1"),
                filterBuilder.In(x => x.Name, new List<string> { "Test5", "Test6", "Test7" })
            );
            await TestMongoOperationWithFilterSizeAsync(filter, 4); // Ne + In + List with 3 elements = 4
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
            await TestMongoOperationWithFilterSizeAsync(filter, 4); // 2 ANDs with 2 children each = 4
        }

        [Fact]
        public void CalculateFilterSizeHandlesDeeplyNestedFilters()
        {
            const int depth = 5000;

            var filter = new BsonDocument();
            var current = filter;
            var expectedFilterSize = 0;

            for (var i = 0; i < depth; i++)
            {
                current[$"value_{i}"] = i;
                expectedFilterSize++;

                var nestedFilter = new BsonDocument();
                current[$"nested_{i}"] = nestedFilter;
                current = nestedFilter;
            }

            current["terminal"] = "done";
            expectedFilterSize++;

            var actualFilterSize = QueryFilterSizeMetricProvider.CalculateFilterSize(filter);

            Assert.Equal(expectedFilterSize, actualFilterSize);
        }

        private async Task TestMongoOperationWithFilterSizeAsync(FilterDefinition<TestDocument> filter, int expectedFilterSize)
        {
            if (!MetricProviderRegistrar.TryGetProvider<QueryFilterSizeMetricProvider>(out var provider) || provider == null)
            {
                throw new Exception($"Failed to fetch an instance of {nameof(QueryFilterSizeMetricProvider)}");
            }

            var filterSizeBefore = provider.QueryFilterSize.WithLabels("find", "testCollection", "test").Sum;

            await MongoTestContext.RunAsync(async collection =>
            {
                _ = await collection.Find(filter).ToListAsync();
            });

            var filterSizeAfter = provider.QueryFilterSize.WithLabels("find", "testCollection", "test").Sum;

            // note: take into account other tests with 'find' might change this metric
            Assert.Equal(expectedFilterSize, filterSizeAfter - filterSizeBefore);
        }
    }
}
