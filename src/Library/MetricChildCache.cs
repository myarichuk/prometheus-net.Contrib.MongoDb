using System.Collections.Concurrent;

namespace PrometheusNet.MongoDb;

/// <summary>
/// Caches labeled metric children (<c>Histogram.Child</c>, <c>Counter.Child</c>, ...) per
/// label combination. Every <c>WithLabels(...)</c> call allocates a <c>string[]</c> and does
/// a registry lookup; on a per-command hot path that is pure garbage. The cache pays one
/// lookup per combination and reuses the child afterwards. Cardinality is identical to the
/// prometheus registry itself (one entry per combination you actually observe).
/// </summary>
internal sealed class MetricChildCache<TKey, TChild>
    where TKey : struct
    where TChild : class
{
    private readonly ConcurrentDictionary<TKey, TChild> _children = new();

    private readonly Func<TKey, TChild> _factory;

    public MetricChildCache(Func<TKey, TChild> factory) => _factory = factory;

    public TChild Get(TKey key) => _children.GetOrAdd(key, _factory);
}
