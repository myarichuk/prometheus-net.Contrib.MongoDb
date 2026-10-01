# prometheus-net.Contrib.MongoDb

Client-side Prometheus instrumentation for the MongoDB C# Driver. It hooks the driver's
command and connection events and exports command, cursor, connection, and query-shape
metrics — no sidecar process, no server polling.

- [Overview](#overview)
- [Why client-side?](#why-client-side)
- [Requirements](#requirements)
- [Installation](#installation)
- [Usage](#usage)
- [Metrics exposed](#metrics-exposed)
- [Grafana dashboard](#grafana-dashboard)
- [Performance](#performance)
  - [Benchmarks](#benchmarks)
  - [Memory usage](#memory-usage)
  - [CPU load](#cpu-load)
  - [Metric cardinality](#metric-cardinality)
- [Custom metric providers](#custom-metric-providers)
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

**Note:** A Grafana dashboard definition is in progress.

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

var settings = MongoClientSettings.FromConnectionString("your_connection_string_here");
settings = settings.InstrumentForPrometheus();

var client = new MongoClient(settings);
```

You can attach extra driver event handling through the optional configurator, which runs
before this library subscribes its own handlers:

```cs
settings = settings.InstrumentForPrometheus(cluster =>
{
    cluster.Subscribe<MongoDB.Driver.Core.Events.CommandStartedEvent>(e => ...);
});
```

## Metrics exposed

| Metric | Type | Labels | What it tells you |
|---|---|---|---|
| `mongodb_client_command_duration` | Histogram (s) | `command_type`, `status`, `target_collection`, `target_db` | How long commands take, split by success/failure |
| `mongodb_client_command_errors_total` | Summary | `command_type`, `error_type`, `target_collection`, `target_db` | Which commands fail, and how |
| `mongodb_client_command_request_size` | Histogram (bytes) | `command_type`, `target_collection`, `target_db` | Wire size of outgoing commands |
| `mongodb_client_command_response_size` | Histogram (bytes) | `command_type`, `target_collection`, `target_db` | Wire size of replies |
| `mongodb_client_query_count` | Counter | `query_type`, `target_collection`, `target_db` | `find`/`aggregate` volume (spike and N+1 detection) |
| `mongodb_client_query_filter_size` | Histogram (clauses) | `query_type`, `target_collection`, `target_db` | Filter complexity (e.g. giant `$in` lists) |
| `mongodb_client_open_cursors_count` | Gauge | `target_collection`, `target_db` | Currently open cursors |
| `mongodb_client_open_cursors_duration` | Histogram (s) | `target_collection`, `target_db` | How long cursors stay open |
| `mongodb_client_cursor_document_count` | Summary | `target_collection`, `target_db` | Documents fetched per cursor (sum to get the total) |
| `mongodb_client_connection_creation_rate` | Counter | `cluster_id`, `end_point` | Connection churn |
| `mongodb_client_connection_duration` | Histogram (s) | `cluster_id`, `end_point` | How long connections live |

> **Note:** In MongoDB, a single logical operation can surface as several commands that share
> an `operationId` (e.g. `find` followed by `getMore` calls paging through results). Cursor
> document counts are aggregated per operation before being published, so cardinality stays flat.

> **Note:** Query filter size recursively counts leaf clauses/items in the filter (or the pipeline stages for `aggregate`), which is a
> useful proxy for query complexity — but actual performance still depends on indexes (or their
> absence).

## Grafana dashboard

A ready-made dashboard covering all the metrics above (command rate/latency/errors, queries,
payload sizes, cursors and connections) is in
[`grafana/mongodb-dashboard.json`](grafana/mongodb-dashboard.json). In Grafana use
**Dashboards → Import**, upload the file and pick your Prometheus data source. The `db`,
`collection` and `cluster` variables filter every panel.

## Performance

### Benchmarks

Overhead is measured with BenchmarkDotNet in [`benchmarks/`](benchmarks/), running paired
scenarios (plain driver client vs. fully instrumented client) against an in-process
MongoDB wire-protocol double, so the numbers below are client-side overhead only.

```
dotnet run -c Release --project benchmarks/prometheus-net.Contrib.MongoDb.Benchmarks -- --filter "*"
```

<!-- BENCHMARK-RESULTS:START -->
**Environment:** BenchmarkDotNet v0.15.8, Windows 11 on Hyper-V (virtualized — absolute times
move between runs, so treat time deltas as approximate), Intel Core Ultra 7 155H, .NET 10.0.9.
Backend is an in-process MongoDB wire-protocol double, so absolute times are dominated by the
fake round-trip: the **delta columns are the instrumentation cost**. Allocation deltas are the
stable signal.

| Scenario | Plain (mean) | Instrumented (mean) | Added time/op | Added alloc/op |
|---|---|---|---|---|
| InsertOne | 322.8 us | 387.4 us | +64.6 us | +6.39 KB |
| InsertMany x100 | 669.7 us | 760.9 us | +91.2 us | +34.78 KB |
| FindOne | 1,161.1 us | 1,143.8 us | within noise | +9.48 KB |
| FindBulk x1000 docs | 3,165.4 us | 3,615.3 us | +449.9 us | +10.74 KB (~11 B/doc) |
| FindFiltered | 2,967.9 us | 2,509.5 us | within noise | within noise |
| FailingFind | 437.9 us | 471.9 us | +34.0 us | +7.86 KB |

Takeaways:

- Single-digit KB and tens of microseconds per command on the hot path (events are pooled,
  metric children are cached, sizes are counted without materializing buffers, filter walks
  use rented arrays).
- Bulk replies scale at roughly a dozen bytes per document — replies are never copied,
  only scanned for cursor bookkeeping and measured with a counting stream.
- GC pressure is unchanged on the bulk path (identical Gen0/Gen1/Gen2 counts
  with and without instrumentation).
<!-- BENCHMARK-RESULTS:END -->

### Memory usage

The library is designed to add as little garbage as possible on the hot path:

- Commands are correlated between driver callbacks by a small struct (database, collection,
  operation type, sizes) — full command and reply documents are never copied or retained.
  Scalar fields are read straight off the driver's `BsonDocument`; request/response sizes
  are measured with a counting stream instead of serializing to `byte[]`.
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
Prometheus server. The library labels by `target_collection` and `target_db`, which — with
many unique collections or databases — can generate a large number of time series
(e.g. 10,000 collections x 5,000 databases is 50M potential series; in practice only
combinations you actually touch are created, but watch this on multi-tenant systems).

Recommendations:

- If you run a high-throughput service, run the benchmarks above to measure the exact
  overhead in your environment.
- Be mindful of the number of collections and databases when using this library.

## Custom metric providers

The `IMongoDbClientMetricProvider` interface is public: implement `Handle(...)` for the
event types you care about and register your assembly with
`MetricProviderRegistrar.RegisterAll(...)`. Only overridden handlers are subscribed, so
providers pay dispatch cost solely for the events they handle. Keep handlers fast and
allocation-free — they run on the driver's event thread — and do not retain event
document references.

## Testing

Tests run against [Mongo.Fakes](https://github.com/myarichuk/Mongo.Fakes), an in-process
MongoDB wire-protocol double: no `mongod` binary download, and the full suite runs in
about a second. This is deliberate — the library only observes driver events, so a real
server would add nothing but time and flakes:

```
dotnet test
```

## Contributing

If you'd like to contribute, please fork the repository and use a feature branch. Pull requests are warmly welcome.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
