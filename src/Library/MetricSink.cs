using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Prometheus;

namespace PrometheusNet.MongoDb;

/// <summary>
/// Creates every metric on the backends a session is configured for: a prometheus-net
/// registry, a <see cref="Meter"/>, or both. Providers record through the children it
/// returns and never see which backend is on.
/// <para/>
/// A metric is named once, in Prometheus form. Its <see cref="Meter"/> instrument is named
/// <c>mongodb.client.</c> plus the rest of that name, with a <c>_seconds</c> or <c>_bytes</c>
/// suffix moved to the unit and <c>_total</c> dropped, so an OpenTelemetry Prometheus
/// exporter produces exactly the prometheus-net names and the dashboard works with either.
/// </summary>
internal sealed class MetricSink
{
    internal const string InFlightCommandsName = "mongodb_client_commands_in_flight";

    internal const string ClientLabelName = "client";

    private const string PrometheusPrefix = "mongodb_client_";

    private const string InFlightHelp = "Commands sent and still waiting for their reply";

    private readonly IMetricFactory? _prometheus;

    private readonly MeterInstruments? _meter;

    private readonly string? _client;

    /// <param name="client">
    /// When set, every metric carries a <c>client</c> label with this value: a static label on
    /// prometheus-net, a tag on the Meter.
    /// </param>
    public MetricSink(CollectorRegistry? registry, Meter? meter, string? client = null)
    {
        if (registry is null && meter is null)
        {
            throw new ArgumentException("Configure a Prometheus registry, a Meter, or both.");
        }

        Registry = registry;
        _client = string.IsNullOrEmpty(client) ? null : client;
        _prometheus = registry is null ? null : Factory(registry, _client);
        _meter = meter is null ? null : MeterInstruments.For(meter);
    }

    public CollectorRegistry? Registry { get; }

    public CounterFamily Counter(string name, string help, params string[] labelNames) =>
        new(
            _prometheus?.CreateCounter(name, help, new CounterConfiguration { LabelNames = labelNames }),
            _meter?.Get(name, static (meter, n, unit, help) => meter.CreateCounter<double>(n, unit, help), help),
            labelNames,
            _client);

    public HistogramFamily Histogram(string name, string help, double[] buckets, params string[] labelNames) =>
        new(
            _prometheus?.CreateHistogram(name, help, new HistogramConfiguration { Buckets = buckets, LabelNames = labelNames }),
            _meter?.Get(
                name,
                (meter, n, unit, help) => meter.CreateHistogram<double>(n, unit, help, tags: null, new InstrumentAdvice<double> { HistogramBucketBoundaries = buckets }),
                help),
            labelNames,
            _client);

    /// <summary>A gauge moved up and down (<see cref="GaugeChild.Inc"/>, <see cref="GaugeChild.Dec"/>).</summary>
    public GaugeFamily UpDownGauge(string name, string help, params string[] labelNames) =>
        new(
            _prometheus?.CreateGauge(name, help, new GaugeConfiguration { LabelNames = labelNames }),
            _meter?.Get(name, static (meter, n, unit, help) => meter.CreateUpDownCounter<double>(n, unit, help), help),
            null,
            labelNames,
            _client);

    /// <summary>A gauge that is set to a value (<see cref="GaugeChild.Set"/>).</summary>
    public GaugeFamily Gauge(string name, string help, params string[] labelNames) =>
        new(
            _prometheus?.CreateGauge(name, help, new GaugeConfiguration { LabelNames = labelNames }),
            null,
            _meter?.Get(name, static (meter, n, unit, help) => meter.CreateGauge<double>(n, unit, help), help),
            labelNames,
            _client);

    /// <summary>
    /// Runs <paramref name="source"/> each time metrics are collected, on every backend: it
    /// sweeps stale state, and returns its in-flight command count for
    /// <c>mongodb_client_commands_in_flight</c>.
    /// </summary>
    public void OnCollect(ICollectSource source)
    {
        if (Registry is not null)
        {
            PrometheusCollect.For(Registry).Add(source, _client);
        }

        _meter?.Hooks.Add(source, _client);
    }

    private static IMetricFactory Factory(CollectorRegistry registry, string? client)
    {
        var factory = Metrics.WithCustomRegistry(registry);
        return client is null ? factory : factory.WithLabels(new Dictionary<string, string> { [ClientLabelName] = client });
    }

    internal static (string Name, string? Unit) MeterName(string prometheusName)
    {
        var name = prometheusName.StartsWith(PrometheusPrefix, StringComparison.Ordinal)
            ? "mongodb.client." + prometheusName[PrometheusPrefix.Length..]
            : prometheusName;

        return name switch
        {
            _ when name.EndsWith("_total", StringComparison.Ordinal) => (name[..^"_total".Length], null),
            _ when name.EndsWith("_seconds", StringComparison.Ordinal) => (name[..^"_seconds".Length], "s"),
            _ when name.EndsWith("_bytes", StringComparison.Ordinal) => (name[..^"_bytes".Length], "By"),
            _ => (name, null),
        };
    }

    internal static TagList Tags(string[] labelNames, string[] values, string? client)
    {
        var tags = default(TagList);
        if (client is not null)
        {
            tags.Add(ClientLabelName, client);
        }

        for (var i = 0; i < labelNames.Length; i++)
        {
            tags.Add(labelNames[i], values[i]);
        }

        return tags;
    }

    /// <summary>
    /// One set of instruments per <see cref="Meter"/>, shared by every session on it: a
    /// second <c>Create*</c> with the same name would be a duplicate instrument to listeners.
    /// </summary>
    private sealed class MeterInstruments
    {
        private static readonly ConditionalWeakTable<Meter, MeterInstruments> All = new();

        private readonly Meter _meter;

        private readonly ConcurrentDictionary<string, Instrument> _instruments = new();

        private MeterInstruments(Meter meter)
        {
            _meter = meter;
            var (name, _) = MeterName(InFlightCommandsName);
            meter.CreateObservableGauge(name, ObserveInFlight, "{command}", InFlightHelp);
        }

        public CollectHooks Hooks { get; } = new();

        public static MeterInstruments For(Meter meter) => All.GetValue(meter, static m => new MeterInstruments(m));

        private IEnumerable<Measurement<double>> ObserveInFlight()
        {
            var measurements = new List<Measurement<double>>();
            Hooks.Collect((client, inFlight) => measurements.Add(client is null
                ? new Measurement<double>(inFlight)
                : new Measurement<double>(inFlight, new KeyValuePair<string, object?>(ClientLabelName, client))));
            return measurements;
        }

        public T Get<T>(string prometheusName, Func<Meter, string, string?, string, T> create, string help)
            where T : Instrument =>
            (T)_instruments.GetOrAdd(prometheusName, n =>
            {
                var (name, unit) = MeterName(n);
                return create(_meter, name, unit, help);
            });
    }

    /// <summary>
    /// One before-collect callback per registry, shared by its sessions, and one in-flight
    /// gauge per <c>client</c> label value on it (one gauge in all when no client is labelled).
    /// </summary>
    private sealed class PrometheusCollect
    {
        private static readonly ConditionalWeakTable<CollectorRegistry, PrometheusCollect> All = new();

        private readonly CollectHooks _hooks = new();

        private readonly CollectorRegistry _registry;

        // Keyed by client label value, "" for none. Only touched on collection, under the callback.
        private readonly Dictionary<string, Prometheus.Gauge> _inFlight = new();

        private PrometheusCollect(CollectorRegistry registry)
        {
            _registry = registry;
            registry.AddBeforeCollectCallback(Collect);
        }

        public static PrometheusCollect For(CollectorRegistry registry) => All.GetValue(registry, static r => new PrometheusCollect(r));

        public void Add(ICollectSource source, string? client)
        {
            lock (_inFlight)
            {
                // Created up front, so the gauge exists (at 0) before the first collection.
                InFlight(client);
            }

            _hooks.Add(source, client);
        }

        private void Collect()
        {
            lock (_inFlight)
            {
                // A client whose sessions are all gone reads 0, not its last value.
                foreach (var gauge in _inFlight.Values)
                {
                    gauge.Set(0);
                }

                _hooks.Collect((client, inFlight) => InFlight(client).Inc(inFlight));
            }
        }

        private Prometheus.Gauge InFlight(string? client)
        {
            var key = client ?? string.Empty;
            if (!_inFlight.TryGetValue(key, out var gauge))
            {
                gauge = Factory(_registry, client).CreateGauge(InFlightCommandsName, InFlightHelp);
                _inFlight.Add(key, gauge);
            }

            return gauge;
        }
    }
}

/// <summary>Something that sweeps its state when metrics are collected.</summary>
internal interface ICollectSource
{
    /// <summary>Sweeps, and returns the number of commands still in flight.</summary>
    int Collect(long nowTimestamp);
}

/// <summary>
/// The sessions to run on collection. Held weakly: a session lives as long as the driver
/// cluster whose event subscriptions reference it, and must not be kept alive by metrics.
/// </summary>
internal sealed class CollectHooks
{
    private readonly List<(WeakReference<ICollectSource> Source, string? Client)> _sources = new();

    public void Add(ICollectSource source, string? client)
    {
        lock (_sources)
        {
            _sources.Add((new WeakReference<ICollectSource>(source), client));
        }
    }

    /// <summary>Collects every live session, reporting each one's in-flight count with its client label.</summary>
    public void Collect(Action<string?, int> report)
    {
        var now = Stopwatch.GetTimestamp();
        lock (_sources)
        {
            for (var i = _sources.Count - 1; i >= 0; i--)
            {
                if (_sources[i].Source.TryGetTarget(out var source))
                {
                    report(_sources[i].Client, source.Collect(now));
                }
                else
                {
                    _sources.RemoveAt(i);
                }
            }
        }
    }
}

internal sealed class CounterFamily(Prometheus.Counter? prometheus, Counter<double>? meter, string[] labelNames, string? client)
{
    public CounterChild WithLabels(params string[] values) =>
        new(
            prometheus is null ? null : values.Length == 0 ? prometheus : prometheus.WithLabels(values),
            meter,
            meter is null ? default : MetricSink.Tags(labelNames, values, client));
}

internal sealed class CounterChild(ICounter? prometheus, Counter<double>? meter, TagList tags)
{
    private readonly TagList _tags = tags;

    /// <summary>The prometheus-net value; 0 when only the Meter is on.</summary>
    public double Value => prometheus?.Value ?? 0;

    public void Inc(double increment = 1)
    {
        prometheus?.Inc(increment);
        meter?.Add(increment, in _tags);
    }
}

internal sealed class HistogramFamily(Prometheus.Histogram? prometheus, Histogram<double>? meter, string[] labelNames, string? client)
{
    /// <summary>Every label combination recorded on prometheus-net so far.</summary>
    public IEnumerable<string[]> GetAllLabelValues() => prometheus?.GetAllLabelValues() ?? [];

    public HistogramChild WithLabels(params string[] values) =>
        new(
            prometheus is null ? null : values.Length == 0 ? prometheus : prometheus.WithLabels(values),
            meter,
            meter is null ? default : MetricSink.Tags(labelNames, values, client));
}

internal sealed class HistogramChild(IHistogram? prometheus, Histogram<double>? meter, TagList tags)
{
    private readonly TagList _tags = tags;

    /// <summary>The prometheus-net observation count; 0 when only the Meter is on.</summary>
    public long Count => prometheus?.Count ?? 0;

    /// <summary>The prometheus-net observation sum; 0 when only the Meter is on.</summary>
    public double Sum => prometheus?.Sum ?? 0;

    public void Observe(double value)
    {
        prometheus?.Observe(value);
        meter?.Record(value, in _tags);
    }
}

internal sealed class GaugeFamily(Prometheus.Gauge? prometheus, UpDownCounter<double>? upDown, Gauge<double>? gauge, string[] labelNames, string? client)
{
    public GaugeChild WithLabels(params string[] values) =>
        new(
            prometheus is null ? null : values.Length == 0 ? prometheus : prometheus.WithLabels(values),
            upDown,
            gauge,
            upDown is null && gauge is null ? default : MetricSink.Tags(labelNames, values, client));
}

/// <summary>
/// A gauge child. On the Meter, an up-down gauge only moves (<see cref="Inc"/>, <see cref="Dec"/>)
/// and a set gauge only takes values (<see cref="Set"/>); each ignores the other operations.
/// </summary>
internal sealed class GaugeChild(IGauge? prometheus, UpDownCounter<double>? upDown, Gauge<double>? gauge, TagList tags)
{
    private readonly TagList _tags = tags;

    /// <summary>The prometheus-net value; 0 when only the Meter is on.</summary>
    public double Value => prometheus?.Value ?? 0;

    public void Inc(double increment = 1)
    {
        prometheus?.Inc(increment);
        upDown?.Add(increment, in _tags);
    }

    public void Dec(double decrement = 1)
    {
        prometheus?.Dec(decrement);
        upDown?.Add(-decrement, in _tags);
    }

    public void Set(double value)
    {
        prometheus?.Set(value);
        gauge?.Record(value, in _tags);
    }
}
