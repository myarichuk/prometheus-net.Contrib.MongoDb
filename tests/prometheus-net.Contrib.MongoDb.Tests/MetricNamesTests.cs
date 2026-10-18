using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace PrometheusNet.MongoDb.Tests;

/// <summary>
/// The scrape output itself. The snapshot of every metric and its type guards against
/// accidental renames: renaming or retyping a metric breaks dashboards and alerts, so it has
/// to be a deliberate change to this list. With <c>METRICS_EXPOSITION_PATH</c> set, the scrape
/// is also written there, so CI can lint it with <c>promtool check metrics</c>.
/// </summary>
public class MetricNamesTests
{
    private static readonly string[] Expected =
    [
        "mongodb_client_command_duration_seconds histogram",
        "mongodb_client_command_errors_total counter",
        "mongodb_client_command_request_size_bytes histogram",
        "mongodb_client_command_response_size_bytes histogram",
        "mongodb_client_command_retries_total counter",
        "mongodb_client_commands_in_flight gauge",
        "mongodb_client_connection_lifetime_seconds histogram",
        "mongodb_client_connections_opened_total counter",
        "mongodb_client_cursor_documents histogram",
        "mongodb_client_cursor_lifetime_seconds histogram",
        "mongodb_client_cursors_closed_total counter",
        "mongodb_client_cursors_open gauge",
        "mongodb_client_instrumentation_handler_errors_total counter",
        "mongodb_client_instrumentation_label_overflow_total counter",
        "mongodb_client_instrumentation_orphaned_commands_total counter",
        "mongodb_client_pool_checkout_duration_seconds histogram",
        "mongodb_client_pool_checkout_failures_total counter",
        "mongodb_client_pool_cleared_total counter",
        "mongodb_client_pool_connections gauge",
        "mongodb_client_pool_connections_in_use gauge",
        "mongodb_client_queries_total counter",
        "mongodb_client_query_filter_size histogram",
        "mongodb_client_server_heartbeat_failures_total counter",
        "mongodb_client_server_rtt_seconds gauge",
        "mongodb_client_server_selection_duration_seconds histogram",
        "mongodb_client_server_selection_failures_total counter",
        "mongodb_client_server_type_changes_total counter",
    ];

    [Fact]
    public async Task Exposed_metric_names_and_types_match_the_snapshot()
    {
        await using var fake = await FakeMongo.StartAsync("names", configureInstrumentation: o => o.MeasureFilterComplexity = true);
        var coll = await fake.SeedAsync("names", documents: 5);
        await coll.Find(FilterDefinition<BsonDocument>.Empty, new FindOptions { BatchSize = 2 }).ToListAsync();

        await fake.FailCommandAsync("aggregate", errorCode: 11000);
        await Assert.ThrowsAsync<MongoCommandException>(() => coll.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));

        var exposition = await fake.Session.ExpositionAsync();
        if (Environment.GetEnvironmentVariable("METRICS_EXPOSITION_PATH") is { Length: > 0 } path)
        {
            await File.WriteAllTextAsync(path, exposition);
        }

        var exposed = Regex.Matches(exposition, @"^# TYPE (\S+) (\S+)$", RegexOptions.Multiline)
            .Select(m => $"{m.Groups[1].Value} {m.Groups[2].Value}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(Expected, exposed);
    }

    [Fact]
    public async Task Observation_lands_in_the_right_bucket()
    {
        var replay = new EventReplay("buckets");
        replay.Succeed("count", new BsonDocument("count", "c"), new BsonDocument { { "ok", 1 }, { "n", 0 } }, operationId: 1, duration: TimeSpan.FromMilliseconds(3));

        // Buckets 0.5 ms, 1 ms, 2 ms, 4 ms, ...: 3 ms is above le="0.002" and within le="0.004".
        var exposition = await replay.Session.ExpositionAsync();
        const string labels = "command_type=\"count\",status=\"success\",target_collection=\"c\",target_db=\"buckets\"";
        Assert.Contains($"mongodb_client_command_duration_seconds_bucket{{{labels},le=\"0.002\"}} 0\n", exposition);
        Assert.Contains($"mongodb_client_command_duration_seconds_bucket{{{labels},le=\"0.004\"}} 1\n", exposition);
        Assert.Contains($"mongodb_client_command_duration_seconds_bucket{{{labels},le=\"+Inf\"}} 1\n", exposition);
        Assert.Contains($"mongodb_client_command_duration_seconds_sum{{{labels}}} 0.003\n", exposition);
    }
}
