using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using Prometheus;
// ReSharper disable ComplexConditionExpression
// ReSharper disable MethodTooLong
// ReSharper disable CognitiveComplexity

/// <summary>
/// Provides instrumentation for MongoDB operations, exporting metrics to Prometheus.
/// </summary>
public static class MongoInstrumentation
{
    private static readonly ConcurrentDictionary<int, Dictionary<string, object>> Commands = new();
    private static readonly ConcurrentDictionary<int, Stopwatch> CursorDurationTimers = new();

    /// <summary>
    /// Gets or sets logger delegate for capturing errors and other information.
    /// </summary>
    public static Action<string>? Logger { get; set; }

    /// <summary>
    /// Instruments the given MongoClientSettings for Prometheus metrics.
    /// </summary>
    /// <param name="settings">The MongoClientSettings to instrument.</param>
    /// <returns>The instrumented MongoClientSettings.</returns>
    public static MongoClientSettings InstrumentForPrometheus(this MongoClientSettings settings)
    {
        settings.ClusterConfigurator = cb =>
        {
            cb.Subscribe<CommandStartedEvent>(e => OnCommandStarted(e));
            cb.Subscribe<CommandSucceededEvent>(e => OnCommandSucceeded(e));
            cb.Subscribe<CommandFailedEvent>(e => OnCommandFailed(e));
        };

        return settings;
    }

    private static void OnCommandFailed(CommandFailedEvent e)
    {
        CursorDurationTimers.TryRemove(e.RequestId, out _);

        try
        {
            var operationType = GetOperationType(e.CommandName);
            if (!Commands.TryGetValue(e.RequestId, out var command))
            {
                return;
            }

            var collectionName = GetCollection(e.CommandName, command);
            var databaseName = GetDatabase(command);

            if (operationType == MongoOperationType.Find)
            {
                MetricsStore.OpenCursors.WithLabels(collectionName, databaseName).Dec();
            }

            MetricsStore.CommandDuration
                .WithLabels(operationType.ToString().ToLower(), "failure", GetCollection(e.CommandName, command), GetDatabase(command))
                .Observe(e.Duration.TotalMilliseconds);

            MetricsStore.CommandErrors
                .WithLabels(operationType.ToString().ToLower(), e.Failure.ToString(), GetCollection(e.CommandName, command), GetDatabase(command))
                .Inc();
        }
        catch (Exception ex)
        {
            Logger?.Invoke($"Error in CommandFailedEvent: {ex.Message}");
        }
        finally
        {
            Commands.Remove(e.RequestId, out _);
        }
    }

    private static void OnCommandSucceeded(CommandSucceededEvent e)
    {
        try
        {
            var operationType = GetOperationType(e.CommandName);
            if (!Commands.TryGetValue(e.RequestId, out var command))
            {
                return;
            }

            var collectionName = GetCollection(e.CommandName, command);
            var databaseName = GetDatabase(command);

            if (operationType is
                MongoOperationType.Insert or
                MongoOperationType.Update or
                MongoOperationType.FindAndModify or
                MongoOperationType.Find or
                MongoOperationType.Delete)
            {
                // this condition is a precaution
                if (command.TryGetValue("documents", out var documentsAsObject) &&
                    documentsAsObject is IDictionary documentsAsDictionary)
                {
                    var count = documentsAsDictionary.Count;

                    MetricsStore.CommandSize
                        .WithLabels(e.CommandName, GetCollection(e.CommandName, command), GetDatabase(command))
                        .Observe(count);
                }

                var commandAsJson = JsonSerializer.Serialize(command);
                var estimatedSize = Encoding.UTF8.GetByteCount(commandAsJson);
                MetricsStore.CommandSize
                    .WithLabels(e.CommandName, GetCollection(e.CommandName, command), GetDatabase(command))
                    .Observe(estimatedSize);
            }

            if (CursorDurationTimers.TryRemove(e.RequestId, out var stopwatch))
            {
                stopwatch.Stop();
                var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;

                MetricsStore.CursorOpenDuration
                    .WithLabels(collectionName, databaseName)
                    .Observe(elapsedSeconds);
            }

            MetricsStore.CommandDuration
                .WithLabels(operationType.ToString().ToLower(), "success", collectionName, databaseName)
                .Observe(e.Duration.TotalMilliseconds);

            if (operationType == MongoOperationType.Find)
            {
                MetricsStore.OpenCursors.WithLabels(collectionName, databaseName).Dec();
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
    }

    private static void OnCommandStarted(CommandStartedEvent e)
    {
        try
        {
            var operationType = GetOperationType(e.CommandName);
            var command = e.Command.ToDictionary();
            Commands.TryAdd(e.RequestId, command);
            if (operationType == MongoOperationType.Find)
            {
                CursorDurationTimers.TryAdd(e.RequestId, Stopwatch.StartNew());

                MetricsStore.FindOperations.WithLabels(GetCollection(e.CommandName, command), GetDatabase(command)).Inc();
                MetricsStore.OpenCursors.WithLabels(GetCollection(e.CommandName, command), GetDatabase(command)).Inc();
            }
        }
        catch (Exception ex)
        {
            Logger?.Invoke($"Error in CommandStartedEvent: {ex.Message}");
        }
    }

    private static MongoOperationType GetOperationType(string commandName) =>
        commandName.ToLower() switch
        {
            "insert" => MongoOperationType.Insert,
            "delete" => MongoOperationType.Delete,
            "find" => MongoOperationType.Find,
            "update" => MongoOperationType.Update,
            "aggregate" => MongoOperationType.Aggregate,
            "count" => MongoOperationType.Count,
            "distinct" => MongoOperationType.Distinct,
            "mapreduce" => MongoOperationType.MapReduce,
            "createIndexes" => MongoOperationType.CreateIndex,
            "dropIndexes" => MongoOperationType.DropIndex,
            "create" => MongoOperationType.CreateCollection,
            "drop" => MongoOperationType.DropCollection,
            "listCollections" => MongoOperationType.ListCollections,
            "listIndexes" => MongoOperationType.ListIndexes,
            "findandmodify" => MongoOperationType.FindAndModify,
            "bulkwrite" => MongoOperationType.BulkWrite,
            _ => MongoOperationType.Other,
        };

    private static string GetCollection(string commandName, Dictionary<string, object> command) =>
        command.TryGetValue(commandName, out var collection)
            ? collection.ToString()
            : "no collection";

    private static string GetDatabase(Dictionary<string, object> command) =>
        command.TryGetValue("$db", out var database) ? database.ToString() : "no database";
}