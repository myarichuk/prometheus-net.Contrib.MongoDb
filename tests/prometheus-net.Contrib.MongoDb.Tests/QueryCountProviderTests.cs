using MongoDB.Driver;
using PrometheusNet.MongoDb.Handlers;

namespace PrometheusNet.MongoDb.Tests
{
    public class QueryCountProviderTests
    {
        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        [InlineData(7)]
        public async Task Query_count_metric_should_properly_work(int expectedQueryCount)
        {
            if (!MetricProviderRegistrar.TryGetProvider<QueryCountMetricProvider>(out var provider) || provider == null)
            {
                throw new Exception($"Failed to fetch an instance of {nameof(QueryCountMetricProvider)}");
            }

            var before = provider.QueryCount.WithLabels("find", MongoTestContext.Collection, MongoTestContext.Database).Value;

            await MongoTestContext.RunAsync(async testCollection =>
            {
                for (int i = 0; i < expectedQueryCount; i++)
                {
                    _ = (await testCollection.FindAsync<TestDocument>(Builders<TestDocument>.Filter.Empty)).ToList();
                }
            });

            var after = provider.QueryCount.WithLabels("find", MongoTestContext.Collection, MongoTestContext.Database).Value;

            Assert.Equal(before + expectedQueryCount, after);
        }
    }
}
