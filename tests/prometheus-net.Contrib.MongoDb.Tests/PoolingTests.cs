using MongoDB.Bson;
using Prometheus;
using PrometheusNet.MongoDb.Events;

namespace PrometheusNet.MongoDb.Tests;

/// <summary>
/// Guards the near-zero-overhead machinery: pooled event shells must come back clean,
/// the child cache must share instances, the counting sizer must agree with real
/// serialization, and the pooled filter walk must handle wide filters (ArrayPool growth).
/// </summary>
public class PoolingTests
{
    [Fact]
    public void EventPool_returns_clean_start_events()
    {
        var first = EventPool<MongoCommandEventStart>.Rent();
        first.RequestId = 7;
        first.OperationId = 42;
        first.OperationRawType = "find";
        first.RawRequestSizeInBytes = 123;
        first.OperationType = MongoOperationType.Find;
        first.TargetDatabase = "db";
        first.TargetCollection = "coll";
        first.FilterDocument = new BsonDocument("a", 1);
        first.CommandDocument = new BsonDocument("find", "coll");
        first.CursorId = 9;
        first.KilledCursorIds = new List<long> { 11, 22 };
        first.Duration = TimeSpan.FromSeconds(1);
        first.BatchDocumentCount = 5;
        first.IsFirstBatch = true;
        first.IsFinalBatch = true;
        Assert.NotNull(first.Command);
        EventPool<MongoCommandEventStart>.Return(first);

        var second = EventPool<MongoCommandEventStart>.Rent();
        Assert.Same(first, second);
        Assert.Equal(0, second.RequestId);
        Assert.Equal(0, second.OperationId);
        Assert.Equal(string.Empty, second.OperationRawType);
        Assert.Equal(0, second.RawRequestSizeInBytes);
        Assert.Equal(MongoOperationType.Other, second.OperationType);
        Assert.Equal(string.Empty, second.TargetDatabase);
        Assert.Equal(string.Empty, second.TargetCollection);
        Assert.Null(second.FilterDocument);
        Assert.Null(second.CommandDocument);
        Assert.Null(second.CursorId);
        Assert.Null(second.KilledCursorIds);
        Assert.Null(second.Duration);
        Assert.Null(second.BatchDocumentCount);
        Assert.False(second.IsFirstBatch);
        Assert.False(second.IsFinalBatch);
        Assert.Null(second.Command);
        EventPool<MongoCommandEventStart>.Return(second);
    }

    [Fact]
    public void EventPool_returns_clean_success_events()
    {
        var first = EventPool<MongoCommandEventSuccess>.Rent();
        first.TargetCollection = "coll";
        first.ReplyDocument = new BsonDocument("ok", 1);
        first.CursorId = 3;
        first.BatchDocumentCount = 2;
        first.IsFirstBatch = true;
        Assert.NotNull(first.Reply);
        Assert.True(first.RawReply.Length > 0);
        EventPool<MongoCommandEventSuccess>.Return(first);

        var second = EventPool<MongoCommandEventSuccess>.Rent();
        Assert.Same(first, second);
        Assert.Equal(string.Empty, second.TargetCollection);
        Assert.Null(second.ReplyDocument);
        Assert.Null(second.CursorId);
        Assert.Null(second.BatchDocumentCount);
        Assert.False(second.IsFirstBatch);
        Assert.Null(second.Reply);
        EventPool<MongoCommandEventSuccess>.Return(second);
    }

    [Fact]
    public void EventPool_returns_clean_failure_events()
    {
        var first = EventPool<MongoCommandEventFailure>.Rent();
        first.OperationRawType = "insert";
        first.Failure = new InvalidOperationException("boom");
        EventPool<MongoCommandEventFailure>.Return(first);

        var second = EventPool<MongoCommandEventFailure>.Rent();
        Assert.Same(first, second);
        Assert.Equal(string.Empty, second.OperationRawType);
        Assert.Null(second.Failure);
        EventPool<MongoCommandEventFailure>.Return(second);
    }

    [Fact]
    public void ChildCache_shares_children_per_label_combination()
    {
        var metric = Metrics.CreateHistogram(
            $"pooling_test_histogram_{Guid.NewGuid():N}",
            "test help",
            new HistogramConfiguration { LabelNames = new[] { "a", "b" } });

        var factoryCalls = 0;
        var cache = new MetricChildCache<(string, string), Histogram.Child>(
            key =>
            {
                factoryCalls++;
                return metric.WithLabels(key.Item1, key.Item2);
            });

        var first = cache.Get(("x", "y"));
        var second = cache.Get(("x", "y"));
        var other = cache.Get(("x", "z"));

        Assert.Same(first, second);
        Assert.NotSame(first, other);
        Assert.Equal(2, factoryCalls);

        first.Observe(1);
        Assert.Equal(1, metric.WithLabels("x", "y").Count);
    }

    [Theory]
    [MemberData(nameof(BsonSizeCases))]
    public void SizeCounter_matches_serialization(BsonDocument document)
    {
        var expected = document.ToBson().Length;

        // Twice: the second call reuses the thread-static stream.
        Assert.Equal(expected, BsonSizeCounter.GetSizeInBytes(document));
        Assert.Equal(expected, BsonSizeCounter.GetSizeInBytes(document));
    }

    public static TheoryData<BsonDocument> BsonSizeCases => new()
    {
        new BsonDocument(),
        new BsonDocument("a", 1),
        new BsonDocument
        {
            { "find", "coll" },
            { "filter", new BsonDocument { { "name", "doc-42" }, { "index", new BsonDocument("$gte", 100) } } },
            { "limit", 50 },
            { "$db", "test" },
        },
        new BsonDocument
        {
            { "payload", new string('x', 5000) },
            { "tags", new BsonArray(Enumerable.Range(0, 100).Select(i => (BsonValue)new BsonString($"t-{i}"))) },
            { "nested", new BsonDocument { { "deep", new BsonArray { 1, 2, 3 } } } },
        },
    };

    [Fact]
    public void CalculateFilterSizeHandlesWideFilters()
    {
        // 500 sibling clauses force the pooled walk buffer past its initial rent.
        const int width = 500;
        var filter = new BsonDocument();
        for (var i = 0; i < width; i++)
        {
            filter[$"field_{i}"] = i;
        }

        Assert.Equal(width, PrometheusNet.MongoDb.Handlers.QueryFilterSizeMetricProvider.CalculateFilterSize(filter));
    }
}
