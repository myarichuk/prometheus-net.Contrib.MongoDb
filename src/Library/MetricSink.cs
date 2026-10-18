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

    private const string PrometheusPrefix = "mongodb_client_";

    private const string InFlightHelp = "Commands sent and still waiting for their reply";

    private readonly IMetricFactory? _prometheus;

    private readonly MeterInstruments? _meter;

    public MetricSink(CollectorRegistry? registry, Meter? meter)
    {
        if (registry is null && meter is null)
        {
            throw new ArgumentException("Configure a Prometheus registry, a Meter, or both.");
        }

        Registry = registry;
        _prometheus = registry is null ? null : Metrics.WithCustomRegistry(registry);
        _meter = meter is null ? null : MeterInstruments.For(meter);
    }

    public CollectorRegistry? Registry { get; }

    public CounterFamily Counter(string name, string help, params string[] labelNames) =>
        new(
            _prometheus?.CreateCounter(name, help, new CounterConfiguration { LabelNames = labelNames }),
            _meter?.Get(name, static (meter, n, unit, help) => meter.CreateCounter<double>(n, unit, help), help),
            labelNames);

    public HistogramFamily Histogram(string name, string help, double[] buckets, params string[] labelNames) =>
        new(
            _prometheus?.CreateHistogram(name, help, new HistogramConfiguration { Buckets = buckets, LabelNames = labelNames }),
            _meter?.Get(
                name,
                (meter, n, unit, help) => meter.CreateHistogram<double>(n, unit, help, tags: null, new InstrumentAdvice<double> { HistogramBucketBoundaries = buckets }),
                help),
            labelNames);

    /// <summary>A gauge moved up and down (<see cref="GaugeChild.Inc"/>, <see cref="GaugeChild.Dec"/>).</summary>
    public GaugeFamily UpDownGauge(string name, string help, params string[] labelNames) =>
        new(
            _prometheus?.CreateGauge(name, help, new GaugeConfiguration { LabelNames = labelNames }),
            _meter?.Get(name, static (meter, n, unit, help) => meter.CreateUpDownCounter<double>(n, unit, help), help),
            null,
            labelNames);

    /// <summary>A gauge that is set to a value (<see cref="GaugeChild.Set"/>).</summary>
    public GaugeFamily Gauge(string name, string help, params string[] labelNames) =>
        new(
            _prometheus?.CreateGauge(name, help, new GaugeConfiguration { LabelNames = labelNames }),
            null,
            _meter?.Get(name, static (meter, n, unit, help) => meter.CreateGauge<double>(n, unit, help), help),
            labelNames);

    /// <summary>
    /// Runs <paramref name="source"/> each time metrics are collected, on every backend: it
    /// sweeps stale state, and returns its in-flight command count for
    /// <c>mongodb_client_commands_in_flight</c>.
    /// </summary>
    public void OnCollect(ICollectSource source)
    {
        if (Registry is not null)
        {
            PrometheusCollect.For(Registry).Add(source);
        }

        _meter?.Hooks.Add(source);
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

    internal static TagList Tags(string[] labelNames, string[] values)
    {
        var tags = default(TagList);
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
            meter.CreateObservableGauge(name, () => (double)Hooks.Collect(), "{command}", InFlightHelp);
        }

        public CollectHooks Hooks { get; } = new();

        public static MeterInstruments For(Meter meter) => All.GetValue(meter, static m => new MeterInstruments(m));

        public T Get<T>(string prometheusName, Func<Meter, string, string?, string, T> create, string help)
            where T : Instrument =>
            (T)_instruments.GetOrAdd(prometheusName, n =>
            {
                var (name, unit) = MeterName(n);
                return create(_meter, name, unit, help);
            });
    }

    /// <summary>One before-collect callback and in-flight gauge per registry, shared by its sessions.</summary>
    private sealed class PrometheusCollect
    {
        private static readonly ConditionalWeakTable<CollectorRegistry, PrometheusCollect> All = new();

        private readonly CollectHooks _hooks = new();

        private PrometheusCollect(CollectorRegistry registry)
        {
            var inFlight = Metrics.WithCustomRegistry(registry).CreateGauge(InFlightCommandsName, InFlightHelp);
            registry.AddBeforeCollectCallback(() => inFlight.Set(_hooks.Collect()));
        }

        public static PrometheusCollect For(CollectorRegistry registry) => All.GetValue(registry, static r => new PrometheusCollect(r));

        public void Add(ICollectSource source) => _hooks.Add(source);
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
    private readonly List<WeakReference<ICollectSource>> _sources = new();

    public void Add(ICollectSource source)
    {
        lock (_sources)
        {
            _sources.Add(new WeakReference<ICollectSource>(source));
        }
    }

    public int Collect()
    {
        var now = Stopwatch.GetTimestamp();
        var inFlight = 0;
        lock (_sources)
        {
            for (var i = _sources.Count - 1; i >= 0; i--)
            {
                if (_sources[i].TryGetTarget(out var source))
                {
                    inFlight += source.Collect(now);
                }
                else
                {
                    _sources.RemoveAt(i);
                }
            }
        }

        return inFlight;
    }
}

internal sealed class CounterFamily(Prometheus.Counter? prometheus, Counter<double>? meter, string[] labelNames)
{
    public CounterChild WithLabels(params string[] values) =>
        new(
            prometheus is null ? null : values.Length == 0 ? prometheus : prometheus.WithLabels(values),
            meter,
            meter is null ? default : MetricSink.Tags(labelNames, values));
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

internal sealed class HistogramFamily(Prometheus.Histogram? prometheus, Histogram<double>? meter, string[] labelNames)
{
    /// <summary>Every label combination recorded on prometheus-net so far.</summary>
    public IEnumerable<string[]> GetAllLabelValues() => prometheus?.GetAllLabelValues() ?? [];

    public HistogramChild WithLabels(params string[] values) =>
        new(
            prometheus is null ? null : values.Length == 0 ? prometheus : prometheus.WithLabels(values),
            meter,
            meter is null ? default : MetricSink.Tags(labelNames, values));
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

internal sealed class GaugeFamily(Prometheus.Gauge? prometheus, UpDownCounter<double>? upDown, Gauge<double>? gauge, string[] labelNames)
{
    public GaugeChild WithLabels(params string[] values) =>
        new(
            prometheus is null ? null : values.Length == 0 ? prometheus : prometheus.WithLabels(values),
            upDown,
            gauge,
            upDown is null && gauge is null ? default : MetricSink.Tags(labelNames, values));
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
