using MongoDB.Bson;
using MongoDB.Driver;
using PrometheusNet.Contrib.MongoDb.Handlers;
using PrometheusNet.MongoDb.Handlers;

namespace PrometheusNet.MongoDb.Tests;

/// <summary>
/// Functional tests against <see cref="Mongo.Fakes.Server.MongoFakeServer"/> (in-process
/// wire-protocol double): fast, no <c>mongod</c> binary download. Each test has a server
/// and registry of its own, so the assertions are absolute instead of before/after deltas.
/// </summary>
public class FakeServerInstrumentationTests
{
    private const string Database = "fakesdb";

    [Fact]
    public async Task Insert_records_duration_and_request_response_sizes()
    {
        const string collection = "fake_insert_metrics";
        await using var fake = await FakeMongo.StartAsync(Database);

        await fake.Collection(collection).InsertOneAsync(new BsonDocument { { "name", "a" }, { "n", 1 } });

        Assert.Equal(1, fake.Provider<CommandDurationMetricProvider>().CommandDuration.WithLabels("insert", "success", collection, Database).Count);
        var requestSize = fake.Provider<CommandRequestSizeMetricProvider>().RequestSize.WithLabels("insert", collection, Database);
        Assert.Equal(1, requestSize.Count);
        Assert.True(requestSize.Sum > 0);
        Assert.Equal(1, fake.Provider<CommandResponseSizeProvider>().ResponseSize.WithLabels("insert", collection, Database).Count);
    }

    [Fact]
    public async Task Sizes_can_be_turned_off()
    {
        await using var fake = await FakeMongo.StartAsync(Database, configureInstrumentation: o => o.MeasureSizes = false);

        await fake.Collection("fake_no_sizes").InsertOneAsync(new BsonDocument("n", 1));

        var exposition = await fake.Session.ExpositionAsync();
        Assert.Contains("mongodb_client_command_duration_seconds_count{command_type=\"insert\"", exposition);
        Assert.DoesNotContain("size_bytes", exposition);
    }

    [Fact]
    public async Task Find_records_query_count_filter_size_and_cursor_metrics()
    {
        const string collection = "fake_find_metrics";
        await using var fake = await FakeMongo.StartAsync(Database, configureInstrumentation: o => o.MeasureFilterComplexity = true);
        var coll = fake.Collection(collection);

        await coll.InsertManyAsync(
        [
            new BsonDocument { { "name", "a" } },
            new BsonDocument { { "name", "a" } },
            new BsonDocument { { "name", "b" } },
        ]);

        var found = await coll.Find(Builders<BsonDocument>.Filter.Eq("name", "a")).ToListAsync();

        Assert.Equal(2, found.Count);
        Assert.Equal(1, fake.Provider<QueryCountMetricProvider>().Queries.WithLabels("find", collection, Database).Value);
        Assert.Equal(
            1, // { name: "a" } is a single leaf clause
            fake.Provider<QueryFilterSizeMetricProvider>().QueryFilterSize.WithLabels("find", collection, Database).Sum);

        // Single-batch cursor: opened and closed within the same reply, gauge back to zero.
        var cursors = fake.Provider<CursorMetricsProvider>();
        Assert.Equal(0, cursors.OpenCursors.WithLabels(collection, Database).Value);

        // Single batch of 2 documents observed exactly once.
        Assert.Equal(2, cursors.CursorDocuments.WithLabels(collection, Database).Sum);
        Assert.Equal(1, cursors.CursorDocuments.WithLabels(collection, Database).Count);
    }

    [Fact]
    public async Task Aggregate_records_pipeline_filter_size()
    {
        const string collection = "fake_aggregate_filter_metrics";
        await using var fake = await FakeMongo.StartAsync(Database, configureInstrumentation: o => o.MeasureFilterComplexity = true);
        var coll = fake.Collection(collection);

        await coll.InsertManyAsync(
        [
            new BsonDocument { { "i", 1 } },
            new BsonDocument { { "i", 2 } },
            new BsonDocument { { "i", 3 } },
        ]);

        // Aggregates carry no "filter": their complexity is measured from the pipeline
        // ($gte bound and $limit value are the two leaf clauses here).
        var found = await coll.Aggregate().Match(Builders<BsonDocument>.Filter.Gte("i", 1)).Limit(3).ToListAsync();

        Assert.Equal(3, found.Count);
        Assert.Equal(2, fake.Provider<QueryFilterSizeMetricProvider>().QueryFilterSize.WithLabels("aggregate", collection, Database).Sum);
    }

    [Fact]
    public async Task Failing_find_records_error_and_failure_duration()
    {
        const string collection = "fake_error_metrics";
        await using var fake = await FakeMongo.StartAsync(Database);

        var failure = await Assert.ThrowsAsync<MongoCommandException>(
            () => fake.Collection(collection).Find(new BsonDocument("name", new BsonDocument("$bogusOp", 1))).ToListAsync());

        Assert.NotEmpty(failure.CodeName);
        Assert.Equal(
            1,
            fake.Provider<CommandErrorsMetricProvider>().CommandErrors
                .WithLabels("find", nameof(MongoCommandException), failure.CodeName, CommandErrorsMetricProvider.Server, "false", collection, Database)
                .Value);
        Assert.Equal(1, fake.Provider<CommandDurationMetricProvider>().CommandDuration.WithLabels("find", "failure", collection, Database).Count);
    }
}
