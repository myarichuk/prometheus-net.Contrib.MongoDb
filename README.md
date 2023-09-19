# prometheus-net.MongoDb

## Overview

`prometheus-net.MongoDb` is a C# library that provides client-side Prometheus instrumentation for MongoDB operations (instrumenting MongoDB C# Driver)  
It captures various metrics related to MongoDB commands, errors, and performance, and exports them to Prometheus for monitoring and alerting.

## Features

- Measures MongoDB command durations
- Counts MongoDB find operations
- Tracks the number of open cursors
- Categorizes MongoDB command errors
- Measures MongoDB command sizes
- Measures MongoDB document count in operations

## Metrics Exposed

### Command Duration (`mongodb_command_duration_seconds`)

Histogram metric that measures the duration of MongoDB commands in seconds.

- Labels: `command_type`, `status`, `target_collection`, `target_db`

### Find Operations (`mongodb_find_operations_total`)

Counter metric that counts the total number of MongoDB find operations.

- Labels: `target_collection`, `target_db`

### Open Cursors (`mongodb_open_cursors`)

Gauge metric that tracks the number of open cursors.

- Labels: `target_collection`, `target_db`

### Command Errors (`mongodb_command_errors_total`)

Counter metric that counts the total number of MongoDB command errors.

- Labels: `command_type`, `error_type`, `target_collection`, `target_db`

### Command Size (`mongodb_command_size_bytes`)

Histogram metric that measures the size of MongoDB commands in bytes.

- Labels: `command_type`, `target_collection`, `target_db`

### Command Document Count (`mongodb_command_document_count`)

Histogram metric that measures the document count in MongoDB operations.

- Labels: `command_type`, `target_collection`, `target_db`

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
Install-Package prometheus-net.MongoDb
```

## Contributing

If you'd like to contribute, please fork the repository and use a feature branch. Pull requests are warmly welcome.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

---

Feel free to add or modify any sections as you see fit for your project!