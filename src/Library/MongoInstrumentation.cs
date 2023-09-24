using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Configuration;
using MongoDB.Driver.Core.Events;
using PrometheusNet.Contrib.MongoDb.Events;
using PrometheusNet.MongoDb.Events;
#pragma warning disable SA1503
#pragma warning disable SA1201

// ReSharper disable TooManyDeclarations

// ReSharper disable ComplexConditionExpression
// ReSharper disable MethodTooLong
// ReSharper disable CognitiveComplexity

namespace PrometheusNet.MongoDb;

/// <summary>
/// Provides instrumentation for MongoDB operations, exporting metrics to Prometheus.
/// </summary>
public static class MongoInstrumentation
{
    private static readonly ConcurrentDictionary<int, Dictionary<string, object>> Commands = new();

    static MongoInstrumentation()
    {
        try
        {
            MetricProviderRegistrar.RegisterAll();
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
    }

    /// <summary>
    /// Instruments the given MongoClientSettings for Prometheus metrics.
    /// </summary>
    /// <param name="settings">The MongoClientSettings to instrument.</param>
    /// <returns>The instrumented MongoClientSettings.</returns>
    /// <exception cref="Exception"><see cref="ClusterBuilder"/> delegate in ClusterConfigurator throws an exception</exception>
    public static MongoClientSettings InstrumentForPrometheus(this MongoClientSettings settings)
    {
        var existingConfigurator = settings.ClusterConfigurator;
        settings.ClusterConfigurator = cb =>
        {
            existingConfigurator?.Invoke(cb);

            cb.Subscribe<CommandStartedEvent>(OnCommandStarted);
            cb.Subscribe<CommandSucceededEvent>(OnCommandSucceeded);
            cb.Subscribe<CommandFailedEvent>(OnCommandFailed);
            cb.Subscribe<ConnectionOpenedEvent>(OnConnectionOpened);
            cb.Subscribe<ConnectionFailedEvent>(OnConnectionFailed);
            cb.Subscribe<ConnectionClosedEvent>(OnConnectionClosed);
        };

        return settings;
    }

    private static void OnConnectionClosed(ConnectionClosedEvent @event)
    {
        var connectionEvent = new MongoConnectionClosedEvent
        {
            Endpoint = @event.ServerId.EndPoint.ToString(),
            ClusterId = @event.ServerId.ClusterId.Value,
        };

        EventHub.Default.Publish(connectionEvent);
    }

    private static void OnConnectionFailed(ConnectionFailedEvent @event)
    {
        var connectionEvent = new MongoConnectionFailedEvent
        {
            Endpoint = @event.ServerId.EndPoint.ToString(),
            Exception = @event.Exception,
            ClusterId = @event.ServerId.ClusterId.Value,
        };

        EventHub.Default.Publish(connectionEvent);
    }

    private static void OnConnectionOpened(ConnectionOpenedEvent @event)
    {
        var connectionEvent = new MongoConnectionOpenedEvent
        {
            Endpoint = @event.ServerId.EndPoint.ToString(),
            ClusterId = @event.ServerId.ClusterId.Value,
        };

        EventHub.Default.Publish(connectionEvent);
    }

    private static void OnCommandFailed(CommandFailedEvent e)
    {
        if (Commands.Remove(e.RequestId, out var command))
        {
            var commandEvent = new MongoCommandEventFailure
            {
                RequestId = e.RequestId,
                OperationRawType = e.CommandName,
                Command = command,
                Duration = e.Duration,
                Failure = e.Failure,
                OperationType = GetOperationType(e.CommandName),
                TargetDatabase = GetDatabase(command),
                TargetCollection = GetCollection(e.CommandName, command),
            };
            EventHub.Default.Publish(commandEvent);
        }
    }

    private static void OnCommandSucceeded(CommandSucceededEvent e)
    {
        if (Commands.Remove(e.RequestId, out var command))
        {
            var commandEvent = new MongoCommandEventSuccess
            {
                RequestId = e.RequestId,
                OperationRawType = e.CommandName,
                Command = command,
                Duration = e.Duration,
                OperationType = GetOperationType(e.CommandName),
                TargetDatabase = GetDatabase(command),
                TargetCollection = GetCollection(e.CommandName, command),
                RawReply = e.Reply.ToBson(),
                Reply = e.Reply.ToDictionary(),
                CursorId = long.TryParse(command[e.CommandName].ToString(), out var cursorId) ? cursorId : null,
            };
            EventHub.Default.Publish(commandEvent);
        }
    }

    private static void OnCommandStarted(CommandStartedEvent e)
    {
        var command = e.Command.ToDictionary();
        Commands.TryAdd(e.RequestId, command);

        var commandEvent = new MongoCommandEventStart
        {
            RequestId = e.RequestId,
            OperationRawType = e.CommandName,
            Command = command,
            Duration = null, // no duration yet
            OperationType = GetOperationType(e.CommandName),
            TargetDatabase = GetDatabase(command),
            TargetCollection = GetCollection(e.CommandName, command),
            CursorId = long.TryParse(command[e.CommandName].ToString(), out var cursorId) ? cursorId : null,
        };

        EventHub.Default.Publish(commandEvent);
    }

    private static MongoOperationType GetOperationType(string commandName)
    {
        if (string.Equals(commandName, "insert", StringComparison.OrdinalIgnoreCase)) return MongoOperationType.Insert;
        if (string.Equals(commandName, "delete", StringComparison.OrdinalIgnoreCase)) return MongoOperationType.Delete;
        if (string.Equals(commandName, "find", StringComparison.OrdinalIgnoreCase)) return MongoOperationType.Find;
        if (string.Equals(commandName, "update", StringComparison.OrdinalIgnoreCase)) return MongoOperationType.Update;
        if (string.Equals(commandName, "aggregate", StringComparison.OrdinalIgnoreCase)) return MongoOperationType.Aggregate;
        if (string.Equals(commandName, "count", StringComparison.OrdinalIgnoreCase)) return MongoOperationType.Count;
        if (string.Equals(commandName, "distinct", StringComparison.OrdinalIgnoreCase)) return MongoOperationType.Distinct;
        if (string.Equals(commandName, "mapreduce", StringComparison.OrdinalIgnoreCase)) return MongoOperationType.MapReduce;
        if (string.Equals(commandName, "createindexes", StringComparison.OrdinalIgnoreCase)) return MongoOperationType.CreateIndex;
        if (string.Equals(commandName, "dropindexes", StringComparison.OrdinalIgnoreCase)) return MongoOperationType.DropIndex;
        if (string.Equals(commandName, "create", StringComparison.OrdinalIgnoreCase)) return MongoOperationType.CreateCollection;
        if (string.Equals(commandName, "drop", StringComparison.OrdinalIgnoreCase)) return MongoOperationType.DropCollection;
        if (string.Equals(commandName, "listcollections", StringComparison.OrdinalIgnoreCase)) return MongoOperationType.ListCollections;
        if (string.Equals(commandName, "listindexes", StringComparison.OrdinalIgnoreCase)) return MongoOperationType.ListIndexes;
        if (string.Equals(commandName, "findandmodify", StringComparison.OrdinalIgnoreCase)) return MongoOperationType.FindAndModify;
        if (string.Equals(commandName, "bulkwrite", StringComparison.OrdinalIgnoreCase)) return MongoOperationType.BulkWrite;
        if (string.Equals(commandName, "getmore", StringComparison.OrdinalIgnoreCase)) return MongoOperationType.GetMore;
        if (string.Equals(commandName, "killcursors", StringComparison.OrdinalIgnoreCase)) return MongoOperationType.KillCursors;
        if (string.Equals(commandName, "renameCollection", StringComparison.OrdinalIgnoreCase)) return MongoOperationType.RenameCollection;
        if (string.Equals(commandName, "copydb", StringComparison.OrdinalIgnoreCase)) return MongoOperationType.CopyDb;
        if (string.Equals(commandName, "collMod", StringComparison.OrdinalIgnoreCase)) return MongoOperationType.CollMod;
        if (string.Equals(commandName, "dropDatabase", StringComparison.OrdinalIgnoreCase)) return MongoOperationType.DropDatabase;
        if (string.Equals(commandName, "explain", StringComparison.OrdinalIgnoreCase)) return MongoOperationType.Explain;
        if (string.Equals(commandName, "group", StringComparison.OrdinalIgnoreCase)) return MongoOperationType.Group;
        if (string.Equals(commandName, "geoNear", StringComparison.OrdinalIgnoreCase)) return MongoOperationType.GeoNear;
        if (string.Equals(commandName, "geoSearch", StringComparison.OrdinalIgnoreCase)) return MongoOperationType.GeoSearch;
        if (string.Equals(commandName, "getLastError", StringComparison.OrdinalIgnoreCase)) return MongoOperationType.GetLastError;
        if (string.Equals(commandName, "getPrevError", StringComparison.OrdinalIgnoreCase)) return MongoOperationType.GetPrevError;
        if (string.Equals(commandName, "isMaster", StringComparison.OrdinalIgnoreCase)) return MongoOperationType.IsMaster;
        if (string.Equals(commandName, "listDatabases", StringComparison.OrdinalIgnoreCase)) return MongoOperationType.ListDatabases;
        if (string.Equals(commandName, "reIndex", StringComparison.OrdinalIgnoreCase)) return MongoOperationType.ReIndex;
        if (string.Equals(commandName, "replSetGetStatus", StringComparison.OrdinalIgnoreCase)) return MongoOperationType.ReplSetGetStatus;
        if (string.Equals(commandName, "serverStatus", StringComparison.OrdinalIgnoreCase)) return MongoOperationType.ServerStatus;
        if (string.Equals(commandName, "shardConnPoolStats", StringComparison.OrdinalIgnoreCase)) return MongoOperationType.ShardConnPoolStats;
        if (string.Equals(commandName, "whatsmyuri", StringComparison.OrdinalIgnoreCase)) return MongoOperationType.WhatsMyUri;

        return MongoOperationType.Other;
    }

    private static string GetCollection(string commandName, Dictionary<string, object> command)
    {
        if (command.TryGetValue("collection", out var collectionAsObject))
        {
            return collectionAsObject.ToString();
        }

        return command[commandName].ToString();
    }

    private static string GetDatabase(Dictionary<string, object> command) =>
        command.TryGetValue("$db", out var database) ? database.ToString() : "no database";
}