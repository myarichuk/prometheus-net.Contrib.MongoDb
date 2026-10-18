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
