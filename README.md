# prometheus-net.Contrib.MongoDb

## Overview

`prometheus-net.Contrib.MongoDb` is a C# library that provides client-side Prometheus instrumentation for MongoDB operations (instrumenting MongoDB C# Driver)  
It captures various metrics related to MongoDB commands, errors, and performance, and exports them to Prometheus for monitoring and alerting.

## The Why

Why create another metrics library for MongoDB when there's already [mongodb_exporter](https://github.com/percona/mongodb_exporter)?  
It is true that `mongodb_exporter` is a great tool for monitoring MongoDB, but it is a server-side tool that requires a separate process to run, possibly docker or other mode of deployment. 
On top of that, it "actively" gathers information by polling MongoDB while this exporter will do it "passively" by instrumenting the MongoDB C# Driver, without the need to run a separate process.

**Note:** This library is still in development and more metrics will be added. A grafana dashboard definition is also in-progress :)

## Metrics Exposed

### Command Duration (`mongodb_client_command_duration`)

Histogram metric that measures the duration of MongoDB commands in seconds.

- Labels: `command_type`, `status`, `target_collection`, `target_db`

### Command Duration (Summary) (`mongodb_client_command_duration_summary`)

Summary metric that measures the duration of MongoDB commands in seconds.

- Labels: `command_type`, `status`, `target_collection`, `target_db`

### Open Cursors Count (`mongodb_client_open_cursors_count`)

Gauge metric that tracks the number of open cursors.

- Labels: `target_collection`, `target_db`

### Open Cursors Duration (`mongodb_client_open_cursors_duration`)

Histogram metric that tracks the number of open cursors.

- Labels: `target_collection`, `target_db`

### Open Cursor Document Count (`mongodb_client_cursor_document_count`)

Summary metric that measures the document count fetched by a cursor (sum by operationId to get total per cursor).

- Labels: `operationId`, `target_collection`, `target_db`

> **Note:** In MongoDB, a single operation can be broken into multiple operations that can be grouped by `operationId` (for example find -> getMore that fetch query results in "pages", multiple bulkWrites, etc) 

### Command Errors (`mongodb_client_command_errors_total`)

Counter metric that counts the total number of MongoDB command errors.

- Labels: `command_type`, `error_type`, `target_collection`, `target_db`

### Command Request Size (`mongodb_client_command_request_size`)

Histogram metric that measures the size of MongoDB command request in bytes.

- Labels: `command_type`, `target_collection`, `target_db`

### Command Response Size (`mongodb_client_command_response_size`)

Histogram metric that measures the size of MongoDB command response in bytes.

- Labels: `command_type`, `target_collection`, `target_db`

### Connection Creation Rate (`mongodb_client_connection_creation_rate`)

Counter metric that captures the rate at which new MongoDB connections are created.

- Labels: `cluster_id`, `end_point`

### Connection Duration (`mongodb_client_connection_duration`)

Histogram metric that measures the time it takes to close a MongoDB connection, in seconds.

- Labels: `cluster_id`, `end_point`

### Query Filter Size (`mongodb_client_query_filter_size`)

Histogram metric that tracks the size of MongoDB query filters.

- Labels: `query_type`, `target_collection`, `target_db`

> **Note:** This metric tries to capture the complexity of the filters being used in MongoDB queries. It recursively counts the number of clauses and items in the filters, which could be a useful metric for understanding query performance. Note that the performance in this case also depends on any indexes or their lack in the collection.

### Query Count (`mongodb_client_query_count`)

Counter metric that measures the number of "find" and "aggregate" MongoDB queries.

- Labels: `query_type`, `target_collection`, `target_db`

## Performance Considerations and Overhead

Instrumenting your MongoDB client does come with some level of performance overhead. Below are some factors to consider:

### Memory Usage

The library maintains in-memory metrics related to MongoDB commands, connections, and cursors, among other things. Expect a marginal increase in memory usage.

### CPU Load

The library hooks into various events in the MongoDB driver. Handling these events to generate metrics can cause a slight increase in CPU usage. However, this is generally negligible in a well-optimized application.

### Database Latency

The library instruments driver-side events and thus there should not be increase in MongoDb command latency.

### Metrics Storage and Export

Storing and exporting the metrics to Prometheus will also add some overhead. Make sure your Prometheus instance is capable of handling the load, and consider adjusting the scrape intervals if necessary.

### Metric Cardinality

Metrics with high cardinality can cause increased memory and CPU usage, both on the client and the Prometheus server. The library uses labels like target_collection and target_db which, when dealing with many unique collections or databases, could lead to high cardinality.

#### Example:

If you have 10,000 collections and 5,000 databases, a metric with both these labels could potentially generate 50,000,000 (10,000 x 5,000) unique time series data points. This can significantly impact the performance and resources of your monitoring infrastructure.

### Recommendations

- If you are running a high-throughput service, consider running some benchmarks to measure the exact overhead introduced by this library.
- Be mindful of the size of your collections and your the number of your databases when using this library.

## Usage Example

Here's a simple example to instrument your MongoDB client:

```cs
using MongoDB.Driver;

var settings = MongoClientSettings.FromConnectionString("your_connection_string_here");
settings = settings.InstrumentForPrometheus();

var client = new MongoClient(settings);
```

## Installation

This library is available as a NuGet package. To install, run:

```
Install-Package prometheus-net.Contrib.MongoDb
```

## Contributing

If you'd like to contribute, please fork the repository and use a feature branch. Pull requests are warmly welcome.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
