using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using MongoDB.Bson;
using Prometheus;
using PrometheusNet.Contrib.MongoDb;
using PrometheusNet.MongoDb.Handlers;
using static PrometheusNet.MongoDb.Tests.TestMetrics;

namespace PrometheusNet.MongoDb.Tests;

/// <summary>Label limits, the Meter backend, and clients sharing a registry.</summary>
public class InstrumentationOptionsTests
{
    private static readonly BsonDocument CountReply = new() { { "ok", 1 }, { "n", 0 } };

    [Fact]
    public void Collection_pairs_past_the_limit_are_recorded_as_other()
    {
        var replay = new EventReplay("db", NewSession(o => o.MaxCollectionLabels = 2));
        var duration = replay.Provider<CommandDurationMetricProvider>().CommandDuration;

        foreach (var collection in new[] { "a", "b", "c", "d", "a" })
        {
            replay.Succeed("count", new BsonDocument("count", collection), CountReply, operationId: 1);
        }

        Assert.Equal(2, duration.WithLabels("count", "success", "a", "db").Count);
        Assert.Equal(1, duration.WithLabels("count", "success", "b", "db").Count);
        Assert.Equal(0, duration.WithLabels("count", "success", "c", "db").Count);
        Assert.Equal(2, duration.WithLabels("count", "success", MongoInstrumentationOptions.OverflowLabel, MongoInstrumentationOptions.OverflowLabel).Count);
        Assert.Equal(2, LabelOverflow(replay.Session));
    }

    [Fact]
    public void Normalized_labels_are_what_the_limit_counts()
    {
        var replay = new EventReplay("tenant_1", NewSession(o =>
        {
            o.MaxCollectionLabels = 1;
            o.NormalizeLabels = (database, collection) => (database.StartsWith("tenant_", StringComparison.Ordinal) ? "tenant_*" : database, collection);
        }));
        var other = new EventReplay("tenant_2", replay.Session);

        replay.Succeed("count", new BsonDocument("count", "orders"), CountReply, operationId: 1);
        other.Succeed("count", new BsonDocument("count", "orders"), CountReply, operationId: 2);

        Assert.Equal(2, replay.Provider<CommandDurationMetricProvider>().CommandDuration.WithLabels("count", "success", "orders", "tenant_*").Count);
        Assert.Equal(0, LabelOverflow(replay.Session));
    }

    [Fact]
    public void A_limit_below_one_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => NewSession(o => o.MaxCollectionLabels = 0));

    [Fact]
    public void Some_backend_is_required() =>
        Assert.Throws<ArgumentException>(() => NewSession(o => o.Registry = null));

    [Fact]
    public async Task Clients_on_one_registry_share_its_metrics_and_are_all_swept()
    {
        var registry = Metrics.NewCustomRegistry();
        var first = new EventReplay("db", NewSession(o => o.Registry = registry));
        var second = new EventReplay("db", NewSession(o => o.Registry = registry));

        first.Succeed("count", new BsonDocument("count", "shared"), CountReply, operationId: 1);
        second.Succeed("count", new BsonDocument("count", "shared"), CountReply, operationId: 2);
        first.Start("count", new BsonDocument("count", "shared"), operationId: 3);
        second.Start("count", new BsonDocument("count", "shared"), operationId: 4);

        Assert.Equal(2, second.Provider<CommandDurationMetricProvider>().CommandDuration.WithLabels("count", "success", "shared", "db").Count);
        await registry.CollectAndExportAsTextAsync(Stream.Null);
        Assert.Equal(2, Metrics.WithCustomRegistry(registry).CreateGauge(MetricSink.InFlightCommandsName, string.Empty).Value);
    }

    [Fact]
    public async Task Clients_with_a_client_label_report_separate_series_on_one_registry()
    {
        var registry = Metrics.NewCustomRegistry();
        var orders = new EventReplay("db", NewSession(o =>
        {
            o.Registry = registry;
            o.ClientLabel = "orders-api";
        }));
        var billing = new EventReplay("db", NewSession(o =>
        {
            o.Registry = registry;
            o.ClientLabel = "billing";
        }));
        var unlabelled = new EventReplay("db", NewSession(o => o.Registry = registry));

        orders.Succeed("count", new BsonDocument("count", "shared"), CountReply, operationId: 1);
        orders.Succeed("count", new BsonDocument("count", "shared"), CountReply, operationId: 2);
        billing.Succeed("count", new BsonDocument("count", "shared"), CountReply, operationId: 3);
        unlabelled.Succeed("count", new BsonDocument("count", "shared"), CountReply, operationId: 4);
        orders.Start("count", new BsonDocument("count", "shared"), operationId: 5);
        billing.Start("count", new BsonDocument("count", "shared"), operationId: 6);
        billing.Start("count", new BsonDocument("count", "shared"), operationId: 7);

        var exposition = await orders.Session.ExpositionAsync();
        Assert.Contains("""mongodb_client_command_duration_seconds_count{command_type="count",status="success",target_collection="shared",target_db="db",client="orders-api"} 2""", exposition);
        Assert.Contains("""mongodb_client_command_duration_seconds_count{command_type="count",status="success",target_collection="shared",target_db="db",client="billing"} 1""", exposition);
        Assert.Contains("""mongodb_client_command_duration_seconds_count{command_type="count",status="success",target_collection="shared",target_db="db"} 1""", exposition);
        Assert.Contains("""mongodb_client_commands_in_flight{client="orders-api"} 1""", exposition);
        Assert.Contains("""mongodb_client_commands_in_flight{client="billing"} 2""", exposition);
        Assert.Contains("mongodb_client_commands_in_flight 0", exposition);
    }

    [Fact]
    public void An_empty_client_label_adds_no_label()
    {
        var replay = new EventReplay("db", NewSession(o => o.ClientLabel = string.Empty));
        replay.Succeed("count", new BsonDocument("count", "c"), CountReply, operationId: 1);

        Assert.Equal(1, replay.Provider<CommandDurationMetricProvider>().CommandDuration.WithLabels("count", "success", "c", "db").Count);
    }

    [Fact]
    public void Meter_receives_the_same_metrics_under_dotted_names()
    {
        using var meter = new Meter($"test.{Guid.NewGuid():N}");
        var measurements = new ConcurrentQueue<(Instrument Instrument, double Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter == meter)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Enqueue((instrument, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value))));
        listener.Start();

        // Meter only: no prometheus-net registry at all.
        var replay = new EventReplay("db", NewSession(o =>
        {
            o.Registry = null;
            o.Meter = meter;
            o.ClientLabel = "metered-app";
        }));
        replay.Succeed("count", new BsonDocument("count", "metered"), CountReply, operationId: 1, duration: TimeSpan.FromMilliseconds(4));
        replay.Start("count", new BsonDocument("count", "metered"), operationId: 2);
        listener.RecordObservableInstruments();

        var duration = Assert.Single(measurements, m => m.Instrument.Name == "mongodb.client.command_duration");
        Assert.Equal("s", duration.Instrument.Unit);
        Assert.Equal(0.004, duration.Value, precision: 6);
        Assert.Equal("count", duration.Tags["command_type"]);
        Assert.Equal("metered", duration.Tags["target_collection"]);
        Assert.Equal("metered-app", duration.Tags["client"]);
        Assert.Equal(new MongoInstrumentationOptions().DurationBuckets, ((Histogram<double>)duration.Instrument).Advice?.HistogramBucketBoundaries);

        // Both commands were sent, so both have a request size.
        var sizes = measurements.Where(m => m.Instrument.Name == "mongodb.client.command_request_size").ToArray();
        Assert.Equal(2, sizes.Length);
        Assert.All(sizes, size => Assert.Equal("By", size.Instrument.Unit));
        Assert.All(sizes, size => Assert.True(size.Value > 0));

        var inFlight = Assert.Single(measurements, m => m.Instrument.Name == "mongodb.client.commands_in_flight");
        Assert.Equal(1, inFlight.Value);
        Assert.Equal("metered-app", inFlight.Tags["client"]);
    }

    [Theory]
    [InlineData("mongodb_client_command_duration_seconds", "mongodb.client.command_duration", "s")]
    [InlineData("mongodb_client_command_request_size_bytes", "mongodb.client.command_request_size", "By")]
    [InlineData("mongodb_client_queries_total", "mongodb.client.queries", null)]
    [InlineData("mongodb_client_cursors_open", "mongodb.client.cursors_open", null)]
    public void Meter_names_drop_the_unit_suffix(string prometheusName, string meterName, string? unit) =>
        Assert.Equal((meterName, unit), MetricSink.MeterName(prometheusName));

    private static double LabelOverflow(InstrumentationSession session) =>
        session.Sink.Counter("mongodb_client_instrumentation_label_overflow_total", string.Empty).WithLabels().Value;
}
