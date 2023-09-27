using MongoDB.Driver;
using PrometheusNet.Contrib.MongoDb.Handlers;
// ReSharper disable ComplexConditionExpression

namespace PrometheusNet.MongoDb.Tests
{
    public class OpenCursorDurationTests
    {
        [Fact]
        public async Task Can_measure_open_cursor_duration_in_multiple_batches()
        {
            if (!MetricProviderRegistrar.TryGetProvider<OpenCursorDurationMetricProvider>(out var provider) || 
                provider == null)
            {
                throw new Exception($"Failed to fetch an instance of {nameof(OpenCursorDurationMetricProvider)}");
            }

            await MongoTestContext.RunAsync(async collection =>
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

                var initialCount =
                    provider.OpenCursorDuration
                        .WithLabels("testCollection", "test")
                        .Count;

                _ = (await collection.FindAsync(filter, findOptions)).ToList();

                var afterCount =
                    provider.OpenCursorDuration
                        .WithLabels("testCollection", "test")
                        .Count;

                Assert.True(afterCount > initialCount);
            });
        }

        [Fact]
        public async Task Can_measure_open_cursor_duration_in_single_batches()
        {
            if (!MetricProviderRegistrar.TryGetProvider<OpenCursorDurationMetricProvider>(out var provider) || 
                provider == null)
            {
                throw new Exception($"Failed to fetch an instance of {nameof(OpenCursorDurationMetricProvider)}");
            }

            await MongoTestContext.RunAsync(async collection =>
            {
                for (int i = 0; i < 10; i++)
                {
                    await collection.InsertOneAsync(new TestDocument { Id = i.ToString(), Name = "Test1" });
                }

                var filterBuilder = Builders<TestDocument>.Filter;
                var filter = filterBuilder.Ne(x => x.Id, "1");

                var findOptions = new FindOptions<TestDocument>
                {
                    BatchSize = 25,
                };

                var initialCount =
                    provider.OpenCursorDuration
                        .WithLabels("testCollection", "test")
                        .Count;

                _ = (await collection.FindAsync(filter, findOptions)).ToList();

                var afterCount =
                    provider.OpenCursorDuration
                        .WithLabels("testCollection", "test")
                        .Count;

                Assert.True(afterCount > initialCount);
            });
        }
    }
}