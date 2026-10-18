using System.Collections.Concurrent;
using PrometheusNet.Contrib.MongoDb;

namespace PrometheusNet.MongoDb;

/// <summary>
/// Turns a command's (database, collection) into the labels it is recorded under: the
/// user's normaliser first, then a hard cap on distinct pairs. Past the cap, new pairs are
/// recorded as <see cref="MongoInstrumentationOptions.OverflowLabel"/>. Every per-label
/// cache downstream (<see cref="MetricChildCache{TKey,TChild}"/>, the registry itself) is
/// bounded by this cap, so a client creating collections dynamically cannot leak series.
/// </summary>
internal sealed class CollectionLabels
{
    private const string OverflowLabel = MongoInstrumentationOptions.OverflowLabel;

    // Admitted pairs only; an overflowing pair is never stored, so the map stays capped.
    private readonly ConcurrentDictionary<(string Database, string Collection), byte> _admitted = new();

    private readonly Func<string, string, (string Database, string Collection)>? _normalize;

    private readonly int _maxPairs;

    private readonly CounterChild _overflow;

    public CollectionLabels(MongoInstrumentationOptions options, MetricSink sink)
    {
        if (options.MaxCollectionLabels < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxCollectionLabels, $"{nameof(options.MaxCollectionLabels)} must be at least 1.");
        }

        _normalize = options.NormalizeLabels;
        _maxPairs = options.MaxCollectionLabels;
        _overflow = sink
            .Counter(
                "mongodb_client_instrumentation_label_overflow_total",
                "Commands recorded with database and collection labels \"__other__\" because the limit of distinct pairs was reached")
            .WithLabels();
    }

    public (string Database, string Collection) Resolve(string database, string collection)
    {
        var labels = _normalize is null ? (database, collection) : _normalize(database, collection);
        if (_admitted.ContainsKey(labels))
        {
            return labels;
        }

        // Racing admissions may overshoot the cap by the number of concurrent callers; the
        // bound only has to hold, not be exact.
        if (_admitted.Count < _maxPairs)
        {
            _admitted.TryAdd(labels, 0);
            return labels;
        }

        _overflow.Inc();
        return (OverflowLabel, OverflowLabel);
    }
}
