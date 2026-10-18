# prometheus-net.Contrib.MongoDb

Client-side Prometheus instrumentation for the MongoDB C# Driver. It hooks the driver's
command and connection events and exports command, cursor, connection-pool, server-selection and query-shape
metrics — no sidecar process, no server polling.

- [Overview](#overview)
- [Why client-side?](#why-client-side)
- [Requirements](#requirements)
- [Installation](#installation)
- [Usage](#usage)
  - [Options](#options)
  - [OpenTelemetry / Meter](#opentelemetry--meter)
- [Metrics exposed](#metrics-exposed)
- [Grafana dashboard](#grafana-dashboard)
- [Performance](#performance)
  - [Benchmarks](#benchmarks)
  - [Memory usage](#memory-usage)
  - [CPU load](#cpu-load)
  - [Metric cardinality](#metric-cardinality)
- [Custom metric providers](#custom-metric-providers)
- [Upgrading from 4.x](#upgrading-from-4x)
- [Testing](#testing)
- [Contributing](#contributing)
- [License](#license)

## Overview

`prometheus-net.Contrib.MongoDb` captures metrics related to MongoDB commands, errors,
cursors, connections, and query shapes, and exports them to Prometheus for monitoring
and alerting.

## Why client-side?

Why another metrics library for MongoDB when [mongodb_exporter](https://github.com/percona/mongodb_exporter)
exists? `mongodb_exporter` is a great server-side tool, but it requires a separate process
(possibly Docker or another deployment) and *actively* polls MongoDB. This library works
*passively*: it instruments the MongoDB C# Driver inside your own process, with no extra
deployment.

## Requirements

- .NET 8 or later (the library targets `net8.0`)
- MongoDB C# Driver 3.x (3.12.0 or later)

## Installation

This library is available as a NuGet package. To install, run:

```
Install-Package prometheus-net.Contrib.MongoDb
```

## Usage

Instrument your client settings once at startup:

```cs
using MongoDB.Driver;
using PrometheusNet.Contrib.MongoDb;

var settings = MongoClientSettings.FromConnectionString("your_connection_string_here");
var client = new MongoClient(settings.InstrumentForPrometheus());
```

Each call instruments one client, with its own options and state. Clients that record to
the same registry share its metrics.

### Options

```cs
settings.InstrumentForPrometheus(o =>
{
    o.Registry = myRegistry;               // default: Metrics.DefaultRegistry; null = Meter only
    o.ClientLabel = settings.ApplicationName; // default null: adds client="..." to every series
    o.DurationBuckets = Histogram.ExponentialBuckets(0.001, 2, 14);
    o.NormalizeLabels = (db, coll) => (TenantDb.IsMatch(db) ? "tenant_*" : db, coll);
    o.MaxCollectionLabels = 500;           // default 1000; further pairs are recorded as "__other__"
    o.MeasureSizes = false;                // default true: request/response size histograms
    o.MeasureFilterComplexity = true;      // default false: walks every filter
    o.CursorIdleTimeout = TimeSpan.FromMinutes(31); // match the server's cursorTimeoutMillis (+ grace)
    o.Providers.Add(new MyProvider());     // see Custom metric providers
});
```

`ClusterConfigurator` handlers set on the settings before the call keep running; the
instrumentation adds its own after them.

### Telling clients apart

Several applications (or several clients in one) reporting to one Prometheus are usually
told apart by their scrape target's `job`/`instance` labels. When that is not enough, set
`o.ClientLabel`: every series of that client then carries `client="<value>"`, including
`mongodb_client_commands_in_flight`. Clients on one registry may mix labelled and
unlabelled. The value multiplies every series, so keep it to a fixed handful of names (the
application name, `"primary"`/`"analytics"`), never something per request or per tenant.

### OpenTelemetry / Meter

Set `o.Meter` to also record every metric on a `System.Diagnostics.Metrics.Meter`, e.g. one
from `IMeterFactory`, or set `o.Registry = null` as well to record on the Meter only:

```cs
settings.InstrumentForPrometheus(o =>
{
    o.Meter = meterFactory.Create("MongoDB.Client");
    o.Registry = null;
});
```

Instrument names are the Prometheus names in dotted form with the unit split off:
`mongodb_client_command_duration_seconds` is `mongodb.client.command_duration` with unit `s`,
`..._bytes` has unit `By`, and counters drop `_total`. An OpenTelemetry Prometheus exporter
turns them back into exactly the names below, so the Grafana dashboard works with either
backend. Histograms carry the same buckets as bucket advice.

### Exemplars: from a slow percentile to its trace

When the application traces (OpenTelemetry, or any `ActivitySource` listener), every
command metric carries an exemplar with the `trace_id` and `span_id` of the operation that
recorded it: prometheus-net takes them from `Activity.Current`, and the driver raises its
command events in the operation's own context. If the driver's own tracing is on
(`AddSource("MongoDB.Driver")`), the span is the command's, one per attempt, so a retried
command links each try. Grafana shows them as dots on the duration panels, linked to the
trace.

Nothing needs configuring here; Prometheus needs `--enable-feature=exemplar-storage` and
scrapes them in the OpenMetrics format (which prometheus-net serves when asked). On the
Meter, the OpenTelemetry SDK's own exemplar filter applies.

What no operation caused carries no exemplar: server monitoring (heartbeat failures, server
type changes), the sweeps that run during a scrape (abandoned cursors, orphaned commands),
and events raised under an operation's context after that operation has finished.

## Metrics exposed

| Metric | Type | Labels | What it tells you |
|---|---|---|---|
| `mongodb_client_command_duration_seconds` | Histogram (0.5 ms–8 s) | `command_type`, `status`, `target_collection`, `target_db` | How long commands take, from sending to the reply, split by `success`/`failure` |
| `mongodb_client_command_errors_total` | Counter | `command_type`, `error_type`, `code_name`, `category`, `retryable`, `target_collection`, `target_db` | Which commands fail, and how: the server's code name (`DuplicateKey`, `NotWritablePrimary`, ...; empty when the server never replied), `category` (`timeout`, `network`, `server`, `client`) and `retryable` (`true` for transient errors: network errors, primary changes, shutdowns, `RetryableWriteError`/`TransientTransactionError` labels) |
| `mongodb_client_command_retries_total` | Counter | `command_type`, `target_collection`, `target_db` | Commands the driver retried after a failed attempt (retryable reads and writes). The failed attempt is still counted in the errors above |
| `mongodb_client_commands_in_flight` | Gauge | | Commands sent and still waiting for their reply |
| `mongodb_client_command_request_size_bytes` | Histogram (256 B–64 MB) | `command_type`, `target_collection`, `target_db` | Wire size of outgoing commands (`MeasureSizes`) |
| `mongodb_client_command_response_size_bytes` | Histogram (256 B–64 MB) | `command_type`, `target_collection`, `target_db` | Wire size of replies (`MeasureSizes`) |
| `mongodb_client_queries_total` | Counter | `query_type`, `target_collection`, `target_db` | `find`/`aggregate` volume (spike and N+1 detection) |
| `mongodb_client_query_filter_size` | Histogram (leaf values) | `query_type`, `target_collection`, `target_db` | Filter complexity, e.g. giant `$in` lists (opt-in: `MeasureFilterComplexity`) |
| `mongodb_client_cursors_open` | Gauge | `target_collection`, `target_db` | Currently open server cursors (any command that returns one) |
| `mongodb_client_cursor_lifetime_seconds` | Histogram (1 ms–70 min) | `target_collection`, `target_db` | How long cursors stay open, until exhausted, killed or failed. Single-batch replies never open a cursor and are not recorded |
| `mongodb_client_cursor_documents` | Histogram (1–262,144) | `target_collection`, `target_db` | Documents fetched per cursor across all of its batches; a single-batch reply counts as one cursor |
| `mongodb_client_cursors_closed_total` | Counter | `target_collection`, `target_db`, `reason` | Why cursors ended: `exhausted`, `killed` (disposed early), `server_expired` (getMore got CursorNotFound: consumer too slow), `error`, `abandoned` (idle past `CursorIdleTimeout` and never disposed: a leak) |
| `mongodb_client_connections_opened_total` | Counter | `server` | Connection churn |
| `mongodb_client_connection_lifetime_seconds` | Histogram (1 s–4.5 h) | `server` | How long connections live, until closed or failed |
| `mongodb_client_pool_checkout_duration_seconds` | Histogram (0.1 ms–6.5 s) | `server` | Wait for a pooled connection. Command durations start *after* checkout, so a saturated pool shows up here first |
| `mongodb_client_pool_checkout_failures_total` | Counter | `server`, `reason` | Failed checkouts: `timeout` (pool exhausted for `WaitQueueTimeout`), `connection_error`, `pool_closed` |
| `mongodb_client_pool_connections_in_use` | Gauge | `server` | Connections checked out right now (compare with `MaxConnectionPoolSize`) |
| `mongodb_client_pool_connections` | Gauge | `server` | Pool size, in use or idle |
| `mongodb_client_pool_cleared_total` | Counter | `server` | Pool clears: network error storms, failovers |
| `mongodb_client_server_selection_duration_seconds` | Histogram (0.1 ms–6.5 s) | `operation` | Time to pick a server, before checkout and the command (elections, no primary) |
| `mongodb_client_server_selection_failures_total` | Counter | `operation` | Operations that found no suitable server in time |
| `mongodb_client_server_rtt_seconds` | Gauge | `server` | The driver's averaged round trip time (heartbeat durations are not RTTs: streaming heartbeats include the server's wait) |
| `mongodb_client_server_type_changes_total` | Counter | `server`, `new_type` | Server type changes (`replica_set_primary`, `replica_set_secondary`, `unknown`, ...): elections and outages |
| `mongodb_client_server_heartbeat_failures_total` | Counter | `server` | Failed server monitoring checks |
| `mongodb_client_instrumentation_handler_errors_total` | Counter | `provider` | Exceptions thrown by metric providers (details on the `PrometheusNet-Contrib-MongoDb` EventSource) |
| `mongodb_client_instrumentation_orphaned_commands_total` | Counter | | Commands whose end event never arrived within an hour; dropped unmeasured so per-command state stays bounded |
| `mongodb_client_instrumentation_label_overflow_total` | Counter | | Commands recorded as `__other__` because `MaxCollectionLabels` was reached |

> **Note:** Commands without a collection (`commitTransaction`, `abortTransaction`,
> `listDatabases`, `ping`, database-level `aggregate: 1`, ...) are recorded with
> `target_collection=""`. Commands the driver sends on its own (`hello` heartbeats,
> authentication, `endSessions`) are not recorded.

> **Note:** A cursor spans several commands: the `find`/`aggregate` that opens it, the `getMore`
> calls paging through it, and the `killCursors` the driver sends when the application disposes
> it early (as a separate operation). Cursor metrics follow the cursor (by server and id) across
> all of them, so a cursor is closed exactly once, whichever way it ends, and always under the
> labels of the command that opened it. Abandoned cursors are detected when metrics are
> collected; cursors the server never times out (`noCursorTimeout`, tailable, change streams)
> are never counted as abandoned.

> **Note:** An operation waits in three places, each measured on its own: server selection,
> then pool checkout, then the command itself. `server` labels are `host:port`.

> **Note:** Query filter size counts the leaf values in the filter (or in the pipeline stages for `aggregate`), which is a
> useful proxy for query complexity — but actual performance still depends on indexes (or their
> absence).

## Grafana dashboard

A ready-made dashboard covering every metric above is in
[`grafana/mongodb-dashboard.json`](grafana/mongodb-dashboard.json): commands, errors by
category and code, retries, pool checkout and server selection waits, server RTT and type
changes, queries, cursors and why they closed, payload sizes, connections, and the
instrumentation's own health. In Grafana use **Dashboards → Import**, upload the file and
pick your Prometheus data source. The `client`, `db`, `collection` and `server` variables
filter the panels they apply to; `client` is empty, and filters nothing, unless clients set
`ClientLabel`.

## Performance

### Benchmarks

Overhead is measured with BenchmarkDotNet in [`benchmarks/`](benchmarks/), running paired
scenarios (plain driver client vs. fully instrumented client) against an in-process
MongoDB wire-protocol double, so the numbers below are client-side overhead only.

```
dotnet run -c Release --project benchmarks/prometheus-net.Contrib.MongoDb.Benchmarks -- --filter "*"
```

<!-- BENCHMARK-RESULTS:START -->
**Environment:** BenchmarkDotNet v0.15.8, macOS 26.3 on an Apple M2, .NET 10.0.12.

**Per command** (`PerCommandBenchmarks`): a command's start and success events through the
whole instrumentation and every provider, with no driver or network around it. This is the
precise cost of the library.

| Configuration | Mean per command | Allocated per command |
|---|---|---|
| Building the driver events alone (baseline) | 63 ns | 320 B |
| Default | 605 ns | 432 B |
| `MeasureSizes = false` | 533 ns | 432 B |
| `NormalizeLabels` set | 602 ns | 432 B |
| Collection labels past `MaxCollectionLabels` | 672 ns | 432 B |
| Prometheus and a listened-to `Meter` | 621 ns | 432 B |

**End to end** (`MongoBenchmarks`): plain vs. instrumented client against an in-process
MongoDB wire-protocol double. Absolute times are dominated by the fake round trip, and
allocations include the in-process server, so only time deltas are meaningful, and those
move by ±10 µs between runs.

| Scenario | Plain (mean) | Instrumented (mean) | Added time/op |
|---|---|---|---|
| InsertOne | 95.1 µs | 107.6 µs | +12 µs |
| InsertMany x100 | 328.0 µs | 396.4 µs | within noise (σ ≈ 100 µs) |
| FindOne | 353.4 µs | 377.5 µs | +24 µs |
| FindBulk x1000 docs | 1,406.7 µs | 1,570.3 µs | +164 µs |
| FindFiltered | 1,877.7 µs | 1,752.3 µs | within noise |
| FailingFind | 217.2 µs | 232.1 µs | +15 µs |

Takeaways:

- The instrumentation itself costs about 0.6 µs and 112 B per command (the allocation is the
  entry that correlates a command's start with its end). The label cap, the normaliser and a
  second backend each add at most 10%.
- Commands are read from the driver's wire bytes, never through its document accessors,
  which build a reader per lookup; `AllocationTests` fails if one creeps back in.
- Reply inspection does not grow with batch size: cursor ids and batch counts are read from
  the reply's wire bytes, and sizes from buffer lengths (see `ParsingBenchmarks`).
<!-- BENCHMARK-RESULTS:END -->

### Memory usage

The library is designed to add as little garbage as possible on the hot path:

- Commands are correlated between driver callbacks by a small struct (database, collection,
  operation type, sizes) — full command and reply documents are never copied or retained.
  Only the fields a command type can carry are looked up on the driver's `BsonDocument`.
  Cursor ids and batch sizes are read by walking the reply's wire bytes (element headers
  only, no allocation), and sizes come from the wire buffers' lengths; a counting stream
  is the fallback for materialized values.
- Per-command state lives only between the driver's start and end callbacks, keyed by
  (connection, request id). Cursor/connection trackers only hold entries for actually
  open cursors and connections.
- The in-process event bus dispatches lock-free; handlers run synchronously on the
  driver's event thread, so keep custom providers fast and never retain event document
  references beyond the callback (the driver recycles those buffers).

### CPU load

The library hooks into various events in the MongoDB driver. Handling these events to
generate metrics can cause a slight increase in CPU usage — see the benchmark table above
for measured per-operation costs. This is generally negligible in a well-optimized
application.

### Database latency

The library instruments driver-side events only, so it adds no round trips and should not
increase MongoDB command latency.

### Metrics storage and export

Storing and exporting the metrics to Prometheus also adds overhead. Make sure your
Prometheus instance can handle the load, and consider adjusting scrape intervals if necessary.

### Metric cardinality

Metrics with high cardinality increase memory and CPU usage, both on the client and the
Prometheus server. Command, query and cursor metrics are labelled by `target_db` and
`target_collection`, so a client touching many collections or databases (multi-tenant
systems, collections created per day) creates many series.

The instrumentation bounds this:

- `NormalizeLabels` maps each (database, collection) to the labels it is recorded under,
  e.g. to fold `tenant_123` databases into `tenant_*`.
- `MaxCollectionLabels` (default 1000) caps the distinct pairs after normalisation. New
  pairs past the cap are recorded as `__other__` / `__other__` and counted in
  `mongodb_client_instrumentation_label_overflow_total`, so the client's memory stays
  bounded as well as the series count.
- `ClientLabel` multiplies every series by the number of distinct client names; leave it
  unset when the scrape target already identifies the application.
- `MeasureSizes = false` drops the two size histograms, which are the largest per-pair
  series (each bucket is a series).

## Custom metric providers

The `IMongoDbClientMetricProvider` interface is public: implement `Handle(...)` for the
event types you care about and add an instance to `o.Providers`. It receives the events of
that client only. Command events are this library's own types, with the command already
parsed (operation type, database, collection, cursor ids, sizes); connection,
connection-pool, server-selection and server-monitoring handlers receive the driver's own
event structs as they are. Only overridden handlers are subscribed, so providers pay
dispatch cost solely for the events they handle. Keep handlers fast and allocation-free —
they run on the driver's event thread — and do not retain event document references. An
exception in a provider never reaches the driver; it is counted in
`mongodb_client_instrumentation_handler_errors_total`.

## Upgrading from 4.x

5.0 renames metrics to the Prometheus conventions (base units in the name, `_total` on
counters) and changes buckets, so dashboards and alerts need updating. The dashboard in
this repository is already updated.

| 4.x | 5.0 |
|---|---|
| `mongodb_client_command_duration` | `mongodb_client_command_duration_seconds` (buckets 0.5 ms–8 s) |
| `mongodb_client_command_errors_total` (Summary) | `mongodb_client_command_errors_total` (Counter, more labels) |
| `mongodb_client_command_request_size`, `..._response_size` | `..._request_size_bytes`, `..._response_size_bytes` (256 B–64 MB) |
| `mongodb_client_query_count` | `mongodb_client_queries_total` |
| `mongodb_client_open_cursors_count` | `mongodb_client_cursors_open` |
| `mongodb_client_open_cursors_duration` | `mongodb_client_cursor_lifetime_seconds` (1 ms–70 min) |
| `mongodb_client_cursor_document_count` (Summary) | `mongodb_client_cursor_documents` (Histogram) |
| `mongodb_client_connection_creation_rate` | `mongodb_client_connections_opened_total` |
| `mongodb_client_connection_duration` | `mongodb_client_connection_lifetime_seconds` (1 s–4.5 h) |
| `cluster_id`, `end_point` labels | `server` (`host:port`) |
| `mongodb_client_query_filter_size` on by default | opt-in: `MeasureFilterComplexity = true` |
| `InstrumentForPrometheus(Action<ClusterBuilder>)` | `InstrumentForPrometheus(Action<MongoInstrumentationOptions>)`; set `ClusterConfigurator` yourself before the call |
| `MetricProviderRegistrar.Register(...)`, `RegisterAll(...)` | `o.Providers.Add(...)` |
| `MongoConnectionOpenedEvent` and friends | the driver's `ConnectionOpenedEvent`, `ConnectionClosedEvent`, `ConnectionFailedEvent` |

## Testing

Tests need no `mongod`. End-to-end tests run against
[Mongo.Fakes](https://github.com/myarichuk/Mongo.Fakes), an in-process MongoDB
wire-protocol double with real server-side cursors (`batchSize`, `getMore`, `killCursors`,
idle expiry) and `failCommand` failpoints, so cursor lifecycles and failures are tested
end to end. Edge cases that need an exact reply are tested by replaying real driver events
through the instrumentation's entry points (`EventReplay` in the test project). Every test
instruments its own client on its own registry, so tests run in parallel and assert
absolute values.
Tests of a command path should use the replay harness rather than build library events by
hand, so that they exercise exactly what production builds:

```
dotnet test
```

## Contributing

If you'd like to contribute, please fork the repository and use a feature branch. Pull requests are warmly welcome.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
