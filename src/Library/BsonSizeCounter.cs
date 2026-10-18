using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;

namespace PrometheusNet.MongoDb;

/// <summary>
/// Measures the exact BSON wire size of a document without materializing it.
/// Sizes are computed structurally first (<see cref="RawBson.TryGetSize"/>): O(1) for a
/// wire-encoded document, O(top-level elements) for a materialized command whose payload
/// documents are still wire-encoded. Serializing through a counting stream is the fallback.
/// Serializing to a <c>byte[]</c> just to read <c>Length</c> (the old
/// <c>ToBson().Length</c> pattern) allocates a buffer as large as the payload
/// on every command — megabytes per second of pure garbage on batch-heavy
/// workloads. Counting the bytes as they are written costs the same CPU and
/// zero large-object traffic.
/// <para/>
/// The counting stream is reused per thread: sizing runs synchronously on the
/// dispatching thread, so a single <c>[ThreadStatic]</c> slot is contention-free.
/// </summary>
internal static class BsonSizeCounter
{
    [ThreadStatic]
    private static CountingStream? _cachedStream;

    public static int GetSizeInBytes(BsonDocument document)
    {
        // Wire-encoded documents know their size; only materialized parts are measured.
        if (RawBson.TryGetSize(document, out var size))
        {
            return size;
        }

        return GetSizeBySerializing(document);
    }

    internal static int GetSizeBySerializing(BsonDocument document)
    {
        var counter = _cachedStream ??= new CountingStream();
        counter.Reset();
        using var writer = new BsonBinaryWriter(counter);
        BsonSerializer.Serialize(writer, document);
        return checked((int)counter.BytesWritten);
    }

    // The BSON writer patches document lengths by seeking back, so seeking is
    // supported by tracking a virtual position; only the high-water mark counts.
    // Disposal is a reset, never a close: the stream is meant to be reused.
    private sealed class CountingStream : Stream
    {
        private long _position;

        public long BytesWritten { get; private set; }

        public void Reset()
        {
            _position = 0;
            BytesWritten = 0;
        }

        protected override void Dispose(bool disposing) => Reset();

        public override bool CanRead => false;

        public override bool CanSeek => true;

        public override bool CanWrite => true;

        public override long Length => BytesWritten;

        public override long Position
        {
            get => _position;
            set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => BytesWritten + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            return _position;
        }

        public override void SetLength(long value) =>
            BytesWritten = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));

        public override void Write(byte[] buffer, int offset, int count)
        {
            _position += count;
            if (_position > BytesWritten)
            {
                BytesWritten = _position;
            }
        }

        public override void WriteByte(byte value)
        {
            _position++;
            if (_position > BytesWritten)
            {
                BytesWritten = _position;
            }
        }
    }
}
