using System.Buffers.Binary;
using System.Text;
using MongoDB.Bson;
using MongoDB.Bson.IO;

namespace PrometheusNet.MongoDb;

/// <summary>
/// Reads the few facts the instrumentation needs straight from wire-encoded BSON.
/// The driver hands replies (and most commands) over as <see cref="RawBsonDocument"/>,
/// whose lookups construct a fresh <see cref="BsonBinaryReader"/> per call and whose
/// <see cref="RawBsonArray.Count"/> walks every element through that reader (hundreds of
/// nanoseconds per document). These walkers touch only element headers: skipping a nested
/// document costs one length read, so counting a batch is O(documents) at a few
/// nanoseconds each, and nothing is allocated.
/// <para/>
/// Every walker reports malformed or unexpected input by returning <c>false</c>; callers
/// then fall back to the driver's own (slower, but authoritative) accessors.
/// </summary>
internal static class RawBson
{
    private const byte EndOfDocument = 0x00;
    private const byte DocumentType = 0x03;
    private const byte ArrayType = 0x04;
    private const byte BooleanType = 0x08;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>
    /// Extracts cursor bookkeeping from a reply's <c>cursor</c> sub-document in one pass:
    /// the cursor <c>id</c>, whether the batch is a <c>firstBatch</c>, and the batch size.
    /// </summary>
    /// <returns><c>true</c> when the reply was walked; <paramref name="hasCursor"/> says whether it held a cursor.</returns>
    public static bool TryReadCursor(IByteBuffer reply, out bool hasCursor, out long? cursorId, out bool isFirstBatch, out int? batchCount)
    {
        hasCursor = false;
        cursorId = null;
        isFirstBatch = false;
        batchCount = null;

        var reader = new Reader(reply);
        if (!reader.TryFindElement(0, reply.Length, "cursor"u8, out var type, out var cursorStart) || type != DocumentType)
        {
            // Walked fine but no cursor sub-document: nothing to report.
            return reader.Valid;
        }

        if (!reader.TryReadInt32(cursorStart, out var cursorLength) || cursorLength < 5)
        {
            return false;
        }

        hasCursor = true;
        var end = cursorStart + cursorLength - 1;
        var pos = cursorStart + 4;
        while (pos < end)
        {
            if (!reader.TryReadByte(pos++, out type) || !reader.TryMatchName(ref pos, end, out var name))
            {
                return false;
            }

            var valueStart = pos;
            if (!reader.TrySkipValue(type, ref pos, end))
            {
                return false;
            }

            switch (name)
            {
                case ElementName.Id when type == 0x12 && reader.TryReadInt64(valueStart, out var id64):
                    cursorId = id64;
                    break;
                case ElementName.Id when type == 0x10 && reader.TryReadInt32(valueStart, out var id32):
                    cursorId = id32;
                    break;
                case ElementName.FirstBatch or ElementName.NextBatch when type == ArrayType:
                    if (!reader.TryCountDocuments(valueStart, pos, out var count))
                    {
                        return false;
                    }

                    isFirstBatch = name == ElementName.FirstBatch;
                    batchCount = count;
                    break;
            }
        }

        return pos == end;
    }

    /// <summary>
    /// Reads a <c>find</c> command's <c>noCursorTimeout</c> and <c>tailable</c> flags in one
    /// pass over its top-level elements (two lookups would read a command without them twice).
    /// </summary>
    /// <returns><c>false</c> on malformed input; use the driver's accessors then.</returns>
    public static bool TryReadFindCursorFlags(IByteBuffer command, out bool noCursorTimeout, out bool tailable)
    {
        noCursorTimeout = false;
        tailable = false;

        var reader = new Reader(command);
        if (!reader.TryReadInt32(0, out var length) || length < 5 || length > command.Length)
        {
            return false;
        }

        var end = length - 1;
        var pos = 4;
        while (pos < end)
        {
            if (!reader.TryReadByte(pos++, out var type) || !reader.TryMatchName(ref pos, end, out var name))
            {
                return false;
            }

            if (type == BooleanType && name is ElementName.NoCursorTimeout or ElementName.Tailable)
            {
                if (!reader.TryReadByte(pos, out var value))
                {
                    return false;
                }

                if (name == ElementName.NoCursorTimeout)
                {
                    noCursorTimeout = value != 0;
                }
                else
                {
                    tailable = value != 0;
                }
            }

            if (!reader.TrySkipValue(type, ref pos, end))
            {
                return false;
            }
        }

        return pos == end;
    }

    /// <summary>
    /// Exact serialized BSON size of a document. Wire-encoded documents (and wire-encoded
    /// children of materialized ones, e.g. the <c>documents</c> of an <c>insert</c>) are sized
    /// from their buffer length; only materialized scalars are measured element by element.
    /// </summary>
    /// <returns><c>false</c> for value types this walker does not size; use a serializer then.</returns>
    public static bool TryGetSize(BsonDocument document, out int size)
    {
        size = 0;
        if (document is RawBsonDocument raw)
        {
            size = raw.Slice.Length;
            return true;
        }

        // Other subclasses (LazyBsonDocument, ...) have their own storage rules.
        if (document.GetType() != typeof(BsonDocument))
        {
            return false;
        }

        var total = 5L; // int32 length + terminating null
        foreach (var element in document)
        {
            if (!TryGetValueSize(element.Value, out var valueSize))
            {
                return false;
            }

            total += 2 + Utf8.GetByteCount(element.Name) + valueSize; // type byte + cstring name
        }

        size = checked((int)total);
        return true;
    }

    private static bool TryGetArraySize(BsonArray array, out int size)
    {
        size = 0;
        if (array is RawBsonArray raw)
        {
            size = raw.Slice.Length;
            return true;
        }

        if (array.GetType() != typeof(BsonArray))
        {
            return false;
        }

        var total = 5L;
        for (var i = 0; i < array.Count; i++)
        {
            if (!TryGetValueSize(array[i], out var valueSize))
            {
                return false;
            }

            total += 2 + DigitCount(i) + valueSize; // index names are "0", "1", ...
        }

        size = checked((int)total);
        return true;
    }

    private static bool TryGetValueSize(BsonValue value, out int size)
    {
        switch (value.BsonType)
        {
            case BsonType.Double:
            case BsonType.DateTime:
            case BsonType.Timestamp:
            case BsonType.Int64:
                size = 8;
                return true;
            case BsonType.Int32:
                size = 4;
                return true;
            case BsonType.Boolean:
                size = 1;
                return true;
            case BsonType.ObjectId:
                size = 12;
                return true;
            case BsonType.Decimal128:
                size = 16;
                return true;
            case BsonType.Null:
            case BsonType.Undefined:
            case BsonType.MinKey:
            case BsonType.MaxKey:
                size = 0;
                return true;
            case BsonType.String:
                size = StringSize(value.AsString);
                return true;
            case BsonType.Symbol:
                size = StringSize(value.AsBsonSymbol.Name);
                return true;
            case BsonType.JavaScript:
                size = StringSize(value.AsBsonJavaScript.Code);
                return true;
            case BsonType.RegularExpression:
                var regex = value.AsBsonRegularExpression;
                size = Utf8.GetByteCount(regex.Pattern) + 1 + Utf8.GetByteCount(regex.Options) + 1;
                return true;
#pragma warning disable CS0618 // OldBinary is re-encoded on output, so its stored length is not its wire length.
            case BsonType.Binary when value.AsBsonBinaryData.SubType != BsonBinarySubType.OldBinary:
#pragma warning restore CS0618
                size = 5 + value.AsBsonBinaryData.Bytes.Length; // int32 length + subtype
                return true;
            case BsonType.Document:
                return TryGetSize(value.AsBsonDocument, out size);
            case BsonType.Array:
                return TryGetArraySize(value.AsBsonArray, out size);
            default:
                // OldBinary (re-encoded on output), JavaScriptWithScope: let the serializer decide.
                size = 0;
                return false;
        }
    }

    private static int StringSize(string value) => 4 + Utf8.GetByteCount(value) + 1;

    private static int DigitCount(int value)
    {
        var digits = 1;
        while (value >= 10)
        {
            value /= 10;
            digits++;
        }

        return digits;
    }

    private enum ElementName
    {
        Other,
        Id,
        FirstBatch,
        NextBatch,
        NoCursorTimeout,
        Tailable,
    }

    /// <summary>
    /// Bounds-checked byte access over a (possibly multi-chunk) <see cref="IByteBuffer"/>.
    /// The current contiguous segment is cached, so sequential reads are an array index;
    /// values straddling a chunk boundary are assembled byte by byte.
    /// </summary>
    private struct Reader
    {
        private readonly IByteBuffer _buffer;
        private readonly int _length;
        private byte[]? _segment;
        private int _segmentOffset;
        private int _segmentStart;
        private int _segmentEnd;

        public Reader(IByteBuffer buffer)
        {
            _buffer = buffer;
            _length = buffer.Length;
            _segment = null;
            _segmentOffset = 0;
            _segmentStart = 0;
            _segmentEnd = 0;
            Valid = true;
        }

        /// <summary>False once a walk hit malformed input.</summary>
        public bool Valid { get; private set; }

        public bool TryReadByte(int pos, out byte value)
        {
            if ((uint)pos >= (uint)_length)
            {
                value = 0;
                return Valid = false;
            }

            if (pos < _segmentStart || pos >= _segmentEnd || _segment is null)
            {
                var segment = _buffer.AccessBackingBytes(pos);
                if (segment.Count == 0 || segment.Array is null)
                {
                    value = 0;
                    return Valid = false;
                }

                _segment = segment.Array;
                _segmentOffset = segment.Offset;
                _segmentStart = pos;
                _segmentEnd = pos + segment.Count;
            }

            value = _segment[_segmentOffset + pos - _segmentStart];
            return true;
        }

        public bool TryReadInt32(int pos, out int value)
        {
            value = 0;
            for (var i = 3; i >= 0; i--)
            {
                if (!TryReadByte(pos + i, out var b))
                {
                    return false;
                }

                value = (value << 8) | b;
            }

            return true;
        }

        public bool TryReadInt64(int pos, out long value)
        {
            value = 0;
            if (!TryReadInt32(pos, out var low) || !TryReadInt32(pos + 4, out var high))
            {
                return false;
            }

            value = ((long)high << 32) | (uint)low;
            return true;
        }

        /// <summary>Finds a top-level element of the document at <paramref name="docStart"/>.</summary>
        public bool TryFindElement(int docStart, int limit, ReadOnlySpan<byte> wanted, out byte type, out int valueStart)
        {
            type = 0;
            valueStart = 0;
            if (!TryReadInt32(docStart, out var docLength) || docLength < 5 || docStart + docLength > limit)
            {
                return Valid = false;
            }

            var end = docStart + docLength - 1;
            var pos = docStart + 4;
            while (pos < end)
            {
                if (!TryReadByte(pos++, out type) || !TryNameEquals(ref pos, end, wanted, out var match))
                {
                    return false;
                }

                if (match)
                {
                    valueStart = pos;
                    return true;
                }

                if (!TrySkipValue(type, ref pos, end))
                {
                    return false;
                }
            }

            return false;
        }

        /// <summary>Counts the elements of an array whose elements are all documents.</summary>
        public bool TryCountDocuments(int arrayStart, int arrayEnd, out int count)
        {
            count = 0;
            var end = arrayEnd - 1;
            var pos = arrayStart + 4;
            while (pos < end)
            {
                // Fast path: walk as many documents as fit in the current contiguous segment.
                if (TryReadByte(pos, out _))
                {
                    var segmentStart = _segmentStart;
                    var segment = new ReadOnlySpan<byte>(_segment, _segmentOffset, Math.Min(_segmentEnd, end) - segmentStart);
                    var local = pos - segmentStart;
                    var walked = 0;
                    while (local < segment.Length && segment[local] == DocumentType)
                    {
                        // Names are array indexes ("0", "1", ...): a short scan beats a vectorized search.
                        var lengthAt = local + 1;
                        while (lengthAt < segment.Length && segment[lengthAt] != 0)
                        {
                            lengthAt++;
                        }

                        lengthAt++;
                        if (lengthAt + 4 > segment.Length)
                        {
                            break; // header straddles the segment end: take the slow step
                        }

                        var documentLength = BinaryPrimitives.ReadInt32LittleEndian(segment.Slice(lengthAt, 4));
                        if (documentLength < 5 || documentLength > end - (segmentStart + lengthAt))
                        {
                            return Valid = false;
                        }

                        local = lengthAt + documentLength;
                        walked++;
                    }

                    count += walked;
                    pos = segmentStart + local;
                    if (pos >= end)
                    {
                        break;
                    }
                }

                // Slow step: one document whose header crosses a chunk boundary.
                if (!TryReadByte(pos++, out var type) || type != DocumentType)
                {
                    return false;
                }

                if (!TrySkipName(ref pos, end) || !TrySkipValue(type, ref pos, end))
                {
                    return false;
                }

                count++;
            }

            return pos == end && TryReadByte(end, out var terminator) && terminator == EndOfDocument;
        }

        public bool TryMatchName(ref int pos, int end, out ElementName name)
        {
            var start = pos;
            if (!TrySkipName(ref pos, end))
            {
                name = ElementName.Other;
                return false;
            }

            // Lengths include the terminator: "id" = 3, "firstBatch"/"nextBatch" = 11/10.
            name = (pos - start) switch
            {
                3 when NameIs(start, "id"u8) => ElementName.Id,
                11 when NameIs(start, "firstBatch"u8) => ElementName.FirstBatch,
                10 when NameIs(start, "nextBatch"u8) => ElementName.NextBatch,
                16 when NameIs(start, "noCursorTimeout"u8) => ElementName.NoCursorTimeout,
                9 when NameIs(start, "tailable"u8) => ElementName.Tailable,
                _ => ElementName.Other,
            };
            return true;
        }

        public bool TrySkipValue(byte type, ref int pos, int end)
        {
            int size;
            switch (type)
            {
                case 0x01: // double
                case 0x09: // UTC datetime
                case 0x11: // timestamp
                case 0x12: // int64
                    size = 8;
                    break;
                case 0x10: // int32
                    size = 4;
                    break;
                case 0x08: // boolean
                    size = 1;
                    break;
                case 0x07: // ObjectId
                    size = 12;
                    break;
                case 0x13: // decimal128
                    size = 16;
                    break;
                case 0x06: // undefined
                case 0x0A: // null
                case 0x7F: // max key
                case 0xFF: // min key
                    size = 0;
                    break;
                case 0x02: // string
                case 0x0D: // JavaScript
                case 0x0E: // symbol
                    if (!TryReadInt32(pos, out var stringLength) || stringLength < 1)
                    {
                        return Valid = false;
                    }

                    size = 4 + stringLength;
                    break;
                case 0x0C: // DBPointer: string + ObjectId
                    if (!TryReadInt32(pos, out var pointerLength) || pointerLength < 1)
                    {
                        return Valid = false;
                    }

                    size = 4 + pointerLength + 12;
                    break;
                case 0x05: // binary: int32 length + subtype + bytes
                    if (!TryReadInt32(pos, out var binaryLength) || binaryLength < 0)
                    {
                        return Valid = false;
                    }

                    size = 5 + binaryLength;
                    break;
                case DocumentType:
                case ArrayType:
                case 0x0F: // JavaScript with scope: total length includes itself
                    if (!TryReadInt32(pos, out size) || size < 5)
                    {
                        return Valid = false;
                    }

                    break;
                case 0x0B: // regex: two cstrings
                    return TrySkipName(ref pos, end) && TrySkipName(ref pos, end);
                default:
                    return Valid = false;
            }

            if (size > end - pos)
            {
                return Valid = false;
            }

            pos += size;
            return true;
        }

        private bool TrySkipName(ref int pos, int end)
        {
            while (pos < end)
            {
                if (!TryReadByte(pos++, out var b))
                {
                    return false;
                }

                if (b == 0)
                {
                    return true;
                }
            }

            return Valid = false;
        }

        private bool TryNameEquals(ref int pos, int end, ReadOnlySpan<byte> wanted, out bool match)
        {
            var start = pos;
            match = false;
            if (!TrySkipName(ref pos, end))
            {
                return false;
            }

            match = pos - start == wanted.Length + 1 && NameIs(start, wanted);
            return true;
        }

        private bool NameIs(int start, ReadOnlySpan<byte> wanted)
        {
            for (var i = 0; i < wanted.Length; i++)
            {
                if (!TryReadByte(start + i, out var b) || b != wanted[i])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
