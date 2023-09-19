using System.Collections.Concurrent;
using System.Diagnostics;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using Prometheus;

/// <summary>
/// Provides instrumentation for MongoDB operations, exporting metrics to Prometheus.
/// </summary>
public static class MongoInstrumentation
{
    /// <summary>
    /// Histogram metric for MongoDB open cursor duration (seconds)
    /// </summary>
    public static readonly Histogram CursorOpenDuration = Metrics.CreateHistogram(
    "mongodb_cursor_open_duration_seconds",
    "Duration a MongoDB cursor is open",
    new HistogramConfiguration
    {
        LabelNames = new[] { "target_collection", "target_db" },
    });

    /// <summary>
    /// Histogram metric for MongoDB command durations.
    /// </summary>
    public static readonly Histogram CommandDuration = Metrics.CreateHistogram(
        "mongodb_command_duration_seconds",
        "Duration of MongoDB commands",
        new HistogramConfiguration
        {
            LabelNames = new[] { "command_type", "status", "target_collection", "target_db" },
        });

    /// <summary>
    /// Counter metric for MongoDB find operations.
    /// </summary>
    public static readonly Counter FindOperations = Metrics.CreateCounter(
        "mongodb_find_operations_total",
        "Total number of find operations",
        new CounterConfiguration
        {
            LabelNames = new[] { "target_collection", "target_db" },
        });

    /// <summary>
    /// Gauge metric for MongoDB open cursors.
    /// </summary>
    public static readonly Gauge OpenCursors = Metrics.CreateGauge(
        "mongodb_open_cursors",
        "Number of open cursors",
        new GaugeConfiguration
        {
            LabelNames = new[] { "target_collection", "target_db" },
        });

    /// <summary>
    /// Counter metric for MongoDB command errors.
    /// </summary>
    public static readonly Counter CommandErrors = Metrics.CreateCounter(
        "mongodb_command_errors_total",
        "Total number of command errors",
        new CounterConfiguration
        {
            LabelNames = new[] { "command_type", "error_type", "target_collection", "target_db" },
        });

    private static readonly ConcurrentDictionary<int, Dictionary<string, object>> Commands = new();
    private static readonly ConcurrentDictionary<int, Stopwatch> CursorDurationTimers = new();

    /// <summary>
    /// Logger for capturing errors and other information.
    /// </summary>
    public static Action<string> Logger { get; set; }

    /// <summary>
    /// Instruments the given MongoClientSettings for Prometheus metrics.
    /// </summary>
    /// <param name="settings">The MongoClientSettings to instrument.</param>
    /// <returns>The instrumented MongoClientSettings.</returns>
    public static MongoClientSettings InstrumentForPrometheus(this MongoClientSettings settings)
    {
        settings.ClusterConfigurator = cb =>
        {
            cb.Subscribe<CommandStartedEvent>(e =>
            {
                try
                {
                    var operationType = GetOperationType(e.CommandName);
                    var command = e.Command.ToDictionary();
                    Commands.TryAdd(e.RequestId, command);
                    if (operationType == MongoOperationType.Find)
                    {
                        CursorDurationTimers.TryAdd(e.RequestId, Stopwatch.StartNew());

                        FindOperations.WithLabels(GetCollection(e.CommandName, command), GetDatabase(command)).Inc();
                        OpenCursors.WithLabels(GetCollection(e.CommandName, command), GetDatabase(command)).Inc();
                    }
                }
                catch (Exception ex)
                {
                    Logger?.Invoke($"Error in CommandStartedEvent: {ex.Message}");
                }
            });

            cb.Subscribe<CommandSucceededEvent>(e =>
            {
                try
                {
                    var operationType = GetOperationType(e.CommandName);
                    if (Commands.TryGetValue(e.RequestId, out var command))
                    {
                        var collectionName = GetCollection(e.CommandName, command);
                        var databaseName = GetDatabase(command);

                        if (CursorDurationTimers.TryRemove(e.RequestId, out var stopwatch))
                        {
                            stopwatch.Stop();
                            var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;

                            CursorOpenDuration
                                .WithLabels(collectionName, databaseName)
                                .Observe(elapsedSeconds);
                        }

                        CommandDuration
                            .WithLabels(operationType.ToString().ToLower(), "success", collectionName, databaseName)
                            .Observe(e.Duration.TotalMilliseconds);

                        if (operationType == MongoOperationType.Find)
                        {
                            OpenCursors.WithLabels(collectionName, databaseName).Dec();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger?.Invoke($"Error in CommandSucceededEvent: {ex.Message}");
                }
                finally
                {
                    Commands.Remove(e.RequestId, out _);
                }
            });

            cb.Subscribe<CommandFailedEvent>(e =>
            {
                CursorDurationTimers.TryRemove(e.RequestId, out _);

                try
                {
                    var operationType = GetOperationType(e.CommandName);
                    if (Commands.TryGetValue(e.RequestId, out var command))
                    {
                        CommandDuration
                            .WithLabels(operationType.ToString().ToLower(), "failure", GetCollection(e.CommandName, command), GetDatabase(command))
                            .Observe(e.Duration.TotalMilliseconds);

                        CommandErrors
                            .WithLabels(operationType.ToString().ToLower(), e.Failure.ToString(), GetCollection(e.CommandName, command), GetDatabase(command))
                            .Inc();
                    }
                }
                catch (Exception ex)
                {
                    Logger?.Invoke($"Error in CommandFailedEvent: {ex.Message}");
                }
                finally
                {
                    Commands.Remove(e.RequestId, out _);
                }
            });
        };

        return settings;
    }

    private static MongoOperationType GetOperationType(string commandName) =>
        commandName.ToLower() switch
        {
            "insert" => MongoOperationType.Insert,
            "delete" => MongoOperationType.Delete,
            "find" => MongoOperationType.Find,
            _ => MongoOperationType.Other,
        };

    private static string GetCollection(string commandName, Dictionary<string, object> command) =>
        command.TryGetValue(commandName, out var collection)
            ? collection.ToString()
            : "no collection";

    private static string GetDatabase(Dictionary<string, object> command) =>
        command.TryGetValue("$db", out var database) ? database.ToString() : "no database";
}