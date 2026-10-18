using MongoDB.Bson;
using MongoDB.Bson.IO;

namespace PrometheusNet.MongoDb.Tests;

/// <summary>
/// The raw walkers must agree with the driver's own accessors on every input they accept,
/// including buffers split into chunks at awkward offsets (large replies are multi-chunk).
/// </summary>
public class RawBsonTests
{
    public static TheoryData<int, int> Shapes => new()
    {
        { 0, int.MaxValue },
        { 1, int.MaxValue },
        { 101, int.MaxValue },
        { 1000, int.MaxValue },
        { 101, 7 },  // chunk boundaries inside names, lengths and int64 ids
        { 101, 1 },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void TryReadCursor_matches_driver_view(int documents, int chunkSize)
    {
        foreach (var batchName in new[] { "firstBatch", "nextBatch" })
        {
            var reply = CursorReply(batchName, documents, cursorId: 0x0102_0304_0506_0708);
            var buffer = Buffer(reply.ToBson(), chunkSize);

            Assert.True(RawBson.TryReadCursor(buffer, out var hasCursor, out var cursorId, out var isFirstBatch, out var count));
            Assert.True(hasCursor);
            Assert.Equal(0x0102_0304_0506_0708, cursorId);
            Assert.Equal(batchName == "firstBatch", isFirstBatch);
            Assert.Equal(documents, count);
        }
    }

    [Fact]
    public void TryReadCursor_reads_int32_ids_and_skips_unknown_fields()
    {
        var reply = new BsonDocument
        {
            { "ok", 1.0 },
            { "$clusterTime", new BsonDocument { { "clusterTime", new BsonTimestamp(5) }, { "signature", new BsonBinaryData(new byte[20]) } } },
            {
                "cursor",
                new BsonDocument
                {
                    { "postBatchResumeToken", new BsonDocument("_data", "abc") },
                    { "nextBatch", new BsonArray { new BsonDocument("a", 1) } },
                    { "partialResultsReturned", false },
                    { "id", 0 },
                    { "ns", "db.c" },
                }
            },
            { "operationTime", new BsonTimestamp(9) },
        };

        Assert.True(RawBson.TryReadCursor(Buffer(reply.ToBson(), 3), out var hasCursor, out var cursorId, out var isFirstBatch, out var count));
        Assert.True(hasCursor);
        Assert.Equal(0L, cursorId);
        Assert.False(isFirstBatch);
        Assert.Equal(1, count);
    }

    [Fact]
    public void TryReadCursor_reports_replies_without_cursor()
    {
        var reply = new BsonDocument { { "n", 3 }, { "ok", 1.0 } };

        Assert.True(RawBson.TryReadCursor(Buffer(reply.ToBson(), int.MaxValue), out var hasCursor, out _, out _, out var count));
        Assert.False(hasCursor);
        Assert.Null(count);
    }

    [Fact]
    public void TryReadCursor_declines_batches_of_non_documents()
    {
        var reply = new BsonDocument("cursor", new BsonDocument { { "firstBatch", new BsonArray { 1, 2 } }, { "id", 0L } });

        Assert.False(RawBson.TryReadCursor(Buffer(reply.ToBson(), int.MaxValue), out _, out _, out _, out _));
    }

    [Fact]
    public void TryReadCursor_declines_truncated_input()
    {
        var bytes = CursorReply("firstBatch", 10, 5).ToBson();
        bytes[0] = (byte)(bytes.Length + 50); // claims more bytes than exist

        Assert.False(RawBson.TryReadCursor(Buffer(bytes, int.MaxValue), out _, out _, out _, out _));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(7)]
    [InlineData(1)]
    public void Command_fields_read_raw_match_the_driver_view(int chunkSize)
    {
        var find = new BsonDocument { { "find", "orders.archive" }, { "filter", new BsonDocument("a", 1) }, { "$db", "db" } };
        var getMore = new BsonDocument { { "getMore", 0x0102_0304_0506_0708L }, { "collection", "orders" }, { "$db", "db" } };
        var getMore32 = new BsonDocument { { "getMore", 42 }, { "collection", "orders" } };

        // A value split across chunks is declined (false); callers then use the driver's accessors.
        if (RawBson.TryReadString(Buffer(find.ToBson(), chunkSize), default, out var collection))
        {
            Assert.Equal("orders.archive", collection);
        }

        if (RawBson.TryReadString(Buffer(getMore.ToBson(), chunkSize), "collection"u8, out var getMoreCollection))
        {
            Assert.Equal("orders", getMoreCollection);
        }

        Assert.True(RawBson.TryReadInt64(Buffer(getMore.ToBson(), chunkSize), default, out var cursorId));
        Assert.Equal(0x0102_0304_0506_0708L, cursorId);
        Assert.True(RawBson.TryReadInt64(Buffer(getMore32.ToBson(), chunkSize), default, out var cursorId32));
        Assert.Equal(42, cursorId32);

        // On whole buffers the raw path always answers.
        Assert.True(RawBson.TryReadString(Buffer(find.ToBson(), int.MaxValue), default, out _));
    }

    [Fact]
    public void Missing_or_non_string_fields_read_as_null()
    {
        var bulkWrite = new BsonDocument { { "bulkWrite", 1 }, { "nsInfo", new BsonArray() } }.ToBson();

        Assert.True(RawBson.TryReadString(Buffer(bulkWrite, int.MaxValue), default, out var value));
        Assert.Null(value);
        Assert.True(RawBson.TryReadString(Buffer(bulkWrite, int.MaxValue), "collection"u8, out value));
        Assert.Null(value);
        Assert.True(RawBson.TryReadInt64(Buffer(new BsonDocument("getMore", "x").ToBson(), int.MaxValue), default, out var id));
        Assert.Null(id);
        Assert.True(RawBson.TryReadString(Buffer(new BsonDocument().ToBson(), int.MaxValue), default, out value));
        Assert.Null(value);
        Assert.False(RawBson.TryReadString(Buffer(bulkWrite[..6], int.MaxValue), default, out _));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(5)]
    public void Change_streams_are_recognized_raw(int chunkSize)
    {
        static BsonDocument Aggregate(params BsonDocument[] stages) => new() { { "aggregate", "c" }, { "pipeline", new BsonArray(stages) } };

        Assert.True(RawBson.TryReadIsChangeStream(Buffer(Aggregate(new BsonDocument("$changeStream", new BsonDocument())).ToBson(), chunkSize), out var isChangeStream));
        Assert.True(isChangeStream);
        Assert.True(RawBson.TryReadIsChangeStream(Buffer(Aggregate(new BsonDocument("$match", new BsonDocument())).ToBson(), chunkSize), out isChangeStream));
        Assert.False(isChangeStream);
        Assert.True(RawBson.TryReadIsChangeStream(Buffer(Aggregate().ToBson(), chunkSize), out isChangeStream));
        Assert.False(isChangeStream);
        Assert.True(RawBson.TryReadIsChangeStream(Buffer(Aggregate(new BsonDocument()).ToBson(), chunkSize), out isChangeStream));
        Assert.False(isChangeStream);
    }

    [Fact]
    public void Cached_strings_are_reused_and_never_confused()
    {
        var first = Utf8StringCache.Get("orders"u8);
        Assert.Same(first, Utf8StringCache.Get("orders"u8));
        Assert.Equal("orderz", Utf8StringCache.Get("orderz"u8));

        // Slots are few, so different names do collide; a collision must only cost a miss.
        for (var i = 0; i < 5000; i++)
        {
            var name = $"collection_{i}";
            Assert.Equal(name, Utf8StringCache.Get(System.Text.Encoding.UTF8.GetBytes(name)));
        }
    }

    [Fact]
    public void TryGetSize_matches_serialized_size()
    {
        var document = new BsonDocument
        {
            { "insert", "coll" },
            { "ordered", true },
            { "double", 1.5 },
            { "int64", 5L },
            { "decimal", new BsonDecimal128(1.1m) },
            { "date", new BsonDateTime(DateTime.UnixEpoch) },
            { "ts", new BsonTimestamp(1) },
            { "oid", ObjectId.GenerateNewId() },
            { "null", BsonNull.Value },
            { "undefined", BsonUndefined.Value },
            { "min", BsonMinKey.Value },
            { "max", BsonMaxKey.Value },
            { "regex", new BsonRegularExpression("^a.*", "im") },
            { "js", new BsonJavaScript("return 1") },
            { "symbol", BsonSymbolTable.Lookup("sym") },
            { "binary", new BsonBinaryData(new byte[16], BsonBinarySubType.UuidStandard) }, { "bytes", new BsonBinaryData(new byte[] { 1, 2, 3 }) },
            { "unicode", "żółć ✓" },
            { "nested", new BsonDocument("a", new BsonArray { 1, "x", new BsonDocument("b", 2) }) },
            { "raw", EventReplay.Raw(new BsonDocument("r", 1)) },
            { "documents", new BsonArray(Enumerable.Range(0, 12).Select(i => (BsonValue)EventReplay.Raw(new BsonDocument("i", i)))) },
        };

        foreach (var element in document)
        {
            Assert.True(RawBson.TryGetSize(new BsonDocument(element), out _), element.Name);
        }

        Assert.True(RawBson.TryGetSize(document, out var size));
        Assert.Equal(document.ToBson().Length, size);
        Assert.Equal(size, BsonSizeCounter.GetSizeInBytes(document));
    }

    [Fact]
    public void TryGetSize_declines_values_it_cannot_size()
    {
        var document = new BsonDocument("code", new BsonJavaScriptWithScope("x", new BsonDocument("x", 1)));

        Assert.False(RawBson.TryGetSize(document, out _));
        Assert.Equal(document.ToBson().Length, BsonSizeCounter.GetSizeInBytes(document));
    }

    private static BsonDocument CursorReply(string batchName, int documents, long cursorId) =>
        new()
        {
            {
                "cursor",
                new BsonDocument
                {
                    { batchName, new BsonArray(Enumerable.Range(0, documents).Select(i => new BsonDocument { { "_id", i }, { "s", new string('x', i % 13) } })) },
                    { "id", cursorId },
                    { "ns", "db.coll" },
                }
            },
            { "ok", 1.0 },
        };

    private static IByteBuffer Buffer(byte[] bytes, int chunkSize)
    {
        if (chunkSize >= bytes.Length)
        {
            return new ByteArrayBuffer(bytes, isReadOnly: true);
        }

        var chunks = bytes.Chunk(chunkSize).Select(chunk => (IBsonChunk)new ByteArrayChunk(chunk)).ToList();
        return new MultiChunkBuffer(chunks, bytes.Length, isReadOnly: true);
    }
}
