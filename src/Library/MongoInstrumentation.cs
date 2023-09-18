using System.Collections.Concurrent;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using Prometheus;

public static class MongoInstrumentation
{
    public static readonly Histogram CommandDuration = Metrics.CreateHistogram(
        "mongodb_command_duration_seconds",
        "Duration of MongoDB commands",
        new HistogramConfiguration
        {
            LabelNames = new[] { "command_type", "status" }
        });

    public static readonly Histogram CommandSize = Metrics.CreateHistogram(
        "mongodb_command_size_bytes",
        "Size of MongoDB commands",
        new HistogramConfiguration
        {
            LabelNames = new[] { "command_type" },
            Buckets = new[] { 100.0, 500.0, 1000.0, 5000.0, 10000.0, 50000.0, 100000.0 } // Define your own buckets
        });

    public static readonly Histogram CommandDocumentCount = Metrics.CreateHistogram(
        "mongodb_command_document_count",
        "Size of MongoDB commands",
        new HistogramConfiguration
        {
            LabelNames = new[] { "command_type" },
        });


    private static readonly ConcurrentDictionary<int, BsonDocument> Commands = new ConcurrentDictionary<int, BsonDocument>();

    public static MongoClientSettings InstrumentForPrometheus(this MongoClientSettings settings)
    {
        settings.ClusterConfigurator = cb =>
        {
            cb.Subscribe<CommandStartedEvent>(e =>
            {
                Commands.TryAdd(e.RequestId, e.Command);
            });

            cb.Subscribe<CommandSucceededEvent>(e =>
            {
                if (e.CommandName == "insert" || e.CommandName == "delete" || e.CommandName == "find")
                {
                    if (Commands.TryGetValue(e.RequestId, out var command))
                    {
                        if (command.Contains("documents")) // a precaution
                        {
                            var count = command["documents"].AsBsonArray.Count;
                            CommandSize
                                .WithLabels(e.CommandName)
                                .Observe(count);
                        }

                        var size = Encoding.UTF8.GetByteCount(command.ToJson());
                        CommandSize
                            .WithLabels(e.CommandName)
                            .Observe(size);
                    }
                }

                CommandDuration
                    .WithLabels(e.CommandName, "success")
                    .Observe(e.Duration.TotalMilliseconds);

                Commands.Remove(e.RequestId, out _);
            });

            cb.Subscribe<CommandFailedEvent>(e =>
            {
                CommandDuration
                    .WithLabels(e.CommandName, "failure")
                    .Observe(e.Duration.TotalMilliseconds);

                Commands.Remove(e.RequestId, out _);
            });
        };

        return settings;
    }

    public static void ClearCommands() => Commands.Clear();
}
