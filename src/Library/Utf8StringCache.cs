using System.Text;

namespace PrometheusNet.MongoDb;

/// <summary>
/// Turns UTF-8 bytes read from the wire into strings without allocating for values seen
/// before: collection names repeat on every command. A fixed array of slots indexed by hash;
/// a collision simply replaces the slot, so memory stays bounded whatever the input.
/// Lock-free: slots hold immutable entries, and a reference write is atomic.
/// </summary>
internal static class Utf8StringCache
{
    private const int MaxCachedLength = 128;

    private static readonly Entry?[] Slots = new Entry?[1024];

    public static string Get(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > MaxCachedLength)
        {
            return Encoding.UTF8.GetString(utf8);
        }

        var hash = default(HashCode);
        hash.AddBytes(utf8);
        var slot = hash.ToHashCode() & (Slots.Length - 1);

        var entry = Volatile.Read(ref Slots[slot]);
        if (entry is not null && utf8.SequenceEqual(entry.Utf8))
        {
            return entry.Value;
        }

        var value = Encoding.UTF8.GetString(utf8);
        Volatile.Write(ref Slots[slot], new Entry(utf8.ToArray(), value));
        return value;
    }

    private sealed class Entry(byte[] utf8, string value)
    {
        public byte[] Utf8 { get; } = utf8;

        public string Value { get; } = value;
    }
}
