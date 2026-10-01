using MongoDB.Bson;
using PrometheusNet.Contrib.MongoDb;
using PrometheusNet.MongoDb;
using PrometheusNet.MongoDb.Handlers;

namespace PrometheusNet.MongoDb.Tests;

/// <summary>
/// Covers command-shape parsing in <see cref="MongoInstrumentation"/>: which collection a
/// command targets, which cursor ids a kill names, and which document measures query
/// complexity. These shapes are the contract between driver wire format and metrics.
/// </summary>
public class InstrumentationParsingTests
{
    [Fact]
    public void GetCollection_reads_collection_field()
    {
        // getMore carries the cursor id as its command value, so the collection
        // comes from the "collection" field instead.
        var getMore = new BsonDocument { { "getMore", 123L }, { "collection", "coll" } };

        Assert.Equal("coll", MongoInstrumentation.GetCollection("getMore", getMore));
    }

    [Fact]
    public void GetCollection_reads_command_value()
    {
        Assert.Equal("coll", MongoInstrumentation.GetCollection("find", new BsonDocument("find", "coll")));
        Assert.Equal("coll", MongoInstrumentation.GetCollection("insert", new BsonDocument("insert", "coll")));
        Assert.Equal("coll", MongoInstrumentation.GetCollection("killCursors", new BsonDocument("killCursors", "coll")));
    }

    [Fact]
    public void GetCollection_reads_bulkWrite_namespace()
    {
        var bulkWrite = new BsonDocument
        {
            { "bulkWrite", 1 },
            { "nsInfo", new BsonArray { new BsonDocument("ns", "test.myColl") } },
        };

        Assert.Equal("myColl", MongoInstrumentation.GetCollection("bulkWrite", bulkWrite));
    }

    [Fact]
    public void GetCollection_splits_namespace_on_first_dot()
    {
        // Collection names may legally contain dots; only the first one separates the db.
        var bulkWrite = new BsonDocument
        {
            { "bulkWrite", 1 },
            { "nsInfo", new BsonArray { new BsonDocument("ns", "test.my.dotted.coll") } },
        };

        Assert.Equal("my.dotted.coll", MongoInstrumentation.GetCollection("bulkWrite", bulkWrite));
    }

    [Fact]
    public void GetCollection_attributes_multi_namespace_bulkWrite_to_first_entry()
    {
        var bulkWrite = new BsonDocument
        {
            { "bulkWrite", 1 },
            {
                "nsInfo",
                new BsonArray
                {
                    new BsonDocument("ns", "test.first"),
                    new BsonDocument("ns", "test.second"),
                }
            },
        };

        Assert.Equal("first", MongoInstrumentation.GetCollection("bulkWrite", bulkWrite));
    }

    [Fact]
    public void GetCollection_returns_empty_without_namespace()
    {
        Assert.Equal(
            string.Empty,
            MongoInstrumentation.GetCollection("bulkWrite", new BsonDocument("bulkWrite", 1)));
        Assert.Equal(
            string.Empty,
            MongoInstrumentation.GetCollection(
                "bulkWrite",
                new BsonDocument { { "bulkWrite", 1 }, { "nsInfo", new BsonArray() } }));
        Assert.Equal(string.Empty, MongoInstrumentation.GetCollection("serverStatus", new BsonDocument("serverStatus", 1)));
    }

    [Fact]
    public void GetFilterDocument_passes_find_filter_through()
    {
        var filter = new BsonDocument("name", "a");
        var command = new BsonDocument { { "find", "coll" }, { "filter", filter } };

        Assert.Same(filter, MongoInstrumentation.GetFilterDocument(command, MongoOperationType.Find));
    }

    [Fact]
    public void GetFilterDocument_wraps_aggregate_pipeline()
    {
        var command = new BsonDocument
        {
            { "aggregate", "coll" },
            {
                "pipeline",
                new BsonArray
                {
                    new BsonDocument("$match", new BsonDocument("i", new BsonDocument("$gte", 1))),
                    new BsonDocument("$limit", 3),
                }
            },
        };

        var wrapped = MongoInstrumentation.GetFilterDocument(command, MongoOperationType.Aggregate);

        Assert.NotNull(wrapped);
        // Two leaf clauses: the $gte bound and the $limit value.
        Assert.Equal(2, QueryFilterSizeMetricProvider.CalculateFilterSize(wrapped));
    }

    [Fact]
    public void GetFilterDocument_returns_null_without_filter_or_pipeline()
    {
        Assert.Null(MongoInstrumentation.GetFilterDocument(new BsonDocument("insert", "coll"), MongoOperationType.Insert));
        Assert.Null(MongoInstrumentation.GetFilterDocument(new BsonDocument("aggregate", "coll"), MongoOperationType.Aggregate));
    }
}
