using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace PrometheusNet.MongoDb.Tests;

/// <summary>
/// Snapshot of every metric this library exposes, with its type. Renaming or retyping a
/// metric breaks dashboards and alerts, so it has to be a deliberate change to this list.
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

        var exposition = await fake.Session.ExpositionAsync();
        var exposed = Regex.Matches(exposition, @"^# TYPE (\S+) (\S+)$", RegexOptions.Multiline)
            .Select(m => $"{m.Groups[1].Value} {m.Groups[2].Value}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(Expected, exposed);
    }
}
