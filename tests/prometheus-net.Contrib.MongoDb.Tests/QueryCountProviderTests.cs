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
            await MongoTestContext.RunAsync(async (testCollection, session) =>
            {
                for (int i = 0; i < expectedQueryCount; i++)
                {
                    _ = (await testCollection.FindAsync<TestDocument>(Builders<TestDocument>.Filter.Empty)).ToList();
                }

                var queries = session.Provider<QueryCountMetricProvider>().Queries
                    .WithLabels("find", MongoTestContext.Collection, MongoTestContext.Database);
                Assert.Equal(expectedQueryCount, queries.Value);
            });
        }
    }
}
