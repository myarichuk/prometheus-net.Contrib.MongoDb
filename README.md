# prometheus-net.Contrib.MongoDb

## Overview

`prometheus-net.Contrib.MongoDb` is a C# library that provides client-side Prometheus instrumentation for MongoDB operations (instrumenting MongoDB C# Driver)  
It captures various metrics related to MongoDB commands, errors, and performance, and exports them to Prometheus for monitoring and alerting.

**Note:** This library is still in development and more metrics will be added

## Metrics Exposed

### Command Duration (`mongodb_client_command_duration`)

Histogram metric that measures the duration of MongoDB commands in seconds.

- Labels: `command_type`, `status`, `target_collection`, `target_db`

### Open Cursors Count (`mongodb_client_open_cursors_count`)

Gauge metric that tracks the number of open cursors.

- Labels: `target_collection`, `target_db`

### Open Cursors Duration (`mongodb_client_open_cursors_duration`)

Histogram metric that tracks the number of open cursors.

- Labels: `target_collection`, `target_db`

### Open Cursor Document Count (`mongodb_client_cursor_document_count`)

Summary metric that measures the document count fetched by a cursor.

- Labels: `target_collection`, `target_db`

### Command Errors (`mongodb_client_command_errors_total`)

Counter metric that counts the total number of MongoDB command errors.

- Labels: `command_type`, `error_type`, `target_collection`, `target_db`

### Command Size (`mongodb_command_response_size`)

Histogram metric that measures the size of MongoDB commands in bytes.

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
