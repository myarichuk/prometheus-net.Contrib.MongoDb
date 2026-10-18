using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;

namespace PrometheusNet.MongoDb.Tests;

/// <summary>
/// Exemplars link a metric to the trace that produced it: prometheus-net takes them from
/// <see cref="Activity.Current"/>, so they are right exactly when the instrumentation
/// records in the context of the operation that caused the measurement.
/// </summary>
public sealed class ExemplarTests : IDisposable
{
    // A source of this test class's own, so parallel tests never sample each other's activities.
    private readonly ActivitySource _source = new($"exemplar-tests.{Guid.NewGuid():N}");

    private readonly ActivityListener _listener;

    public ExemplarTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source == _source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _source.Dispose();
    }

    [Fact]
    public async Task Command_metrics_link_to_the_operations_trace()
    {
        await using var fake = await FakeMongo.StartAsync("exemplars");
        await fake.Collection("c").InsertOneAsync(new BsonDocument("n", 1));

        string traceId;
        using (var request = _source.StartActivity("request")!)
        {
            traceId = request.TraceId.ToHexString();
            await fake.Collection("c").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
        }

        var exposition = await fake.Session.OpenMetricsAsync();
        Assert.Contains(Line(exposition, "mongodb_client_command_duration_seconds_bucket{command_type=\"find\",status=\"success\""), l => l.Contains($"trace_id=\"{traceId}\""));
        Assert.Contains(Line(exposition, "mongodb_client_queries_total{query_type=\"find\""), l => l.Contains($"trace_id=\"{traceId}\""));
    }

    [Fact]
    public async Task Sweeps_during_a_scrape_record_no_exemplar()
    {
        var replay = new EventReplay("exemplars");
        var orphan = replay.Start("count", new BsonDocument("count", "c"), operationId: 1);
        Assert.True(replay.Age(orphan, replay.Session.OrphanTimeout + TimeSpan.FromSeconds(1)));

        // The scrape runs in the context of the request that serves /metrics.
        string exposition;
        using (_source.StartActivity("GET /metrics"))
        {
            exposition = await replay.Session.OpenMetricsAsync();
        }

        var orphaned = Assert.Single(Line(exposition, "mongodb_client_instrumentation_orphaned_commands_total "));
        Assert.Equal("mongodb_client_instrumentation_orphaned_commands_total 1.0", orphaned);
    }

    [Fact]
    public async Task Server_monitoring_records_no_exemplar()
    {
        // A client created during a request: its monitoring threads capture the request's
        // context, and keep raising events in it.
        FakeMongo fake;
        using (_source.StartActivity("request"))
        {
            fake = await FakeMongo.StartAsync("exemplars");
            await fake.Collection("c").InsertOneAsync(new BsonDocument("n", 1));
            await TestMetrics.WaitUntilAsync(() => fake.Session.ExpositionAsync().Result.Contains("mongodb_client_server_type_changes_total{"));
        }

        await using (fake)
        {
            var exposition = await fake.Session.OpenMetricsAsync();
            Assert.All(Line(exposition, "mongodb_client_server_type_changes_total{"), l => Assert.DoesNotContain("trace_id", l));
            Assert.Contains(Line(exposition, "mongodb_client_command_duration_seconds_bucket{command_type=\"insert\""), l => l.Contains("trace_id"));
        }
    }

    [Fact]
    public void An_event_under_a_stopped_activity_is_recorded_without_it()
    {
        Activity? seen = null;
        var handler = TraceContext.Live<int>(_ => seen = Activity.Current);

        // A background thread captures the context while an operation runs...
        var operation = _source.StartActivity("operation")!;
        var captured = ExecutionContext.Capture()!;
        handler(0);
        Assert.Same(operation, seen);

        // ...and raises events in it after the operation has finished.
        operation.Stop();
        ExecutionContext.Run(captured, _ =>
        {
            Assert.Same(operation, Activity.Current);
            handler(0);
            Assert.Null(seen);

            // .NET refuses to make a stopped activity current again, so the stale context
            // carries on without one.
            Assert.Null(Activity.Current);
        }, null);
    }

    private static string[] Line(string exposition, string prefix) =>
        exposition.Split('\n').Where(l => l.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
}
