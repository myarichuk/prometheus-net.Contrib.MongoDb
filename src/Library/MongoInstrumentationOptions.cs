using System.Diagnostics.Metrics;
using Prometheus;
using PrometheusNet.MongoDb.Handlers;

namespace PrometheusNet.Contrib.MongoDb;

/// <summary>
/// Configures one instrumented client. Read once, when
/// <see cref="MongoInstrumentation.InstrumentForPrometheus"/>
/// is called; later changes have no effect.
/// </summary>
public sealed class MongoInstrumentationOptions
{
    /// <summary>The default for <see cref="MaxCollectionLabels"/>.</summary>
    public const int DefaultMaxCollectionLabels = 1000;

    /// <summary>The label value that (database, collection) pairs past <see cref="MaxCollectionLabels"/> report as.</summary>
    public const string OverflowLabel = "__other__";

    /// <summary>
    /// The prometheus-net registry to create metrics in, <see cref="Metrics.DefaultRegistry"/>
    /// by default. Set it to null to record on <see cref="Meter"/> only.
    /// </summary>
    public CollectorRegistry? Registry { get; set; } = Metrics.DefaultRegistry;

    /// <summary>
    /// Also records every metric on this <see cref="System.Diagnostics.Metrics.Meter"/>, e.g. one
    /// from <c>IMeterFactory.Create("MongoDB.Client")</c>. Off by default. Instrument names are
    /// the Prometheus names in dotted form (<c>mongodb.client.command_duration</c>, unit <c>s</c>),
    /// so an OpenTelemetry Prometheus exporter reproduces the Prometheus names exactly.
    /// </summary>
    public Meter? Meter { get; set; }

    /// <summary>
    /// Buckets of <c>mongodb_client_command_duration_seconds</c>, 0.5 ms to 8 s by default.
    /// </summary>
    public double[] DurationBuckets { get; set; } = Histogram.ExponentialBuckets(0.0005, 2, 15);

    /// <summary>
    /// Maps a command's (database, collection) to the labels it is recorded under, e.g. to
    /// fold per-tenant databases into one label. Runs on every command, so keep it cheap.
    /// </summary>
    public Func<string, string, (string Database, string Collection)>? NormalizeLabels { get; set; }

    /// <summary>
    /// The most distinct (database, collection) label pairs recorded, after
    /// <see cref="NormalizeLabels"/>. New pairs past the limit are recorded as
    /// <see cref="OverflowLabel"/> and counted in <c>mongodb_client_instrumentation_label_overflow_total</c>,
    /// so a client that creates collections dynamically cannot grow the metrics without bound.
    /// </summary>
    public int MaxCollectionLabels { get; set; } = DefaultMaxCollectionLabels;

    /// <summary>
    /// Records request and reply sizes (<c>mongodb_client_command_request_size_bytes</c>,
    /// <c>mongodb_client_command_response_size_bytes</c>). On by default; turning it off
    /// saves the series, the CPU cost is negligible.
    /// </summary>
    public bool MeasureSizes { get; set; } = true;

    /// <summary>
    /// Records how many leaf values each find filter or aggregation pipeline has
    /// (<c>mongodb_client_query_filter_size</c>). Off by default: it walks every filter.
    /// </summary>
    public bool MeasureFilterComplexity { get; set; }

    /// <summary>
    /// How long a cursor may go without a batch before it counts as abandoned. The server's
    /// default cursor timeout is 10 minutes; the default here adds a minute of grace for a
    /// <c>getMore</c> already in flight. Match it to the server's <c>cursorTimeoutMillis</c>.
    /// </summary>
    public TimeSpan CursorIdleTimeout { get; set; } = TimeSpan.FromMinutes(11);

    /// <summary>
    /// Additional providers that receive this client's events alongside the built-in ones.
    /// </summary>
    public IList<IMongoDbClientMetricProvider> Providers { get; } = new List<IMongoDbClientMetricProvider>();
}
