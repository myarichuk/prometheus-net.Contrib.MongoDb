using System.Collections.Concurrent;
using System.Collections.Frozen;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Configuration;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Events;
using PrometheusNet.Contrib.MongoDb.Events;
using PrometheusNet.MongoDb;
using PrometheusNet.MongoDb.Events;

#pragma warning disable SA1503
#pragma warning disable SA1201

// ReSharper disable TooManyDeclarations
// ReSharper disable TooManyChainedReferences
// ReSharper disable ComplexConditionExpression
// ReSharper disable MethodTooLong
// ReSharper disable CognitiveComplexity
namespace PrometheusNet.Contrib.MongoDb;

/// <summary>
/// Provides instrumentation for MongoDB operations, exporting metrics to Prometheus.
/// </summary>
public static class MongoInstrumentation
{
    /// <summary>
    /// Minimal per-command correlation state. Deliberately small: entries live in
    /// <see cref="Commands"/> only between the driver's start and end callbacks, and
    /// orphans (commands whose end event never arrives) must stay cheap.
    /// No command or reply payloads are retained here.
    /// </summary>
    private readonly record struct CommandCorrelation(
        string Database,
        string Collection,
        MongoOperationType OperationType,
        int RequestSizeInBytes,
        long? CursorId);

    // Keyed by (connection, request): request ids are scoped per connection, so a bare
    // request id can collide across concurrent connections and miscorrelate metrics.
    private static readonly ConcurrentDictionary<(ConnectionId Connection, int Request), CommandCorrelation> Commands = new();

    // Driver-internal handshake / monitoring commands. The driver sends these on its own
    // (SDAM heartbeats); they carry no user collection and would only add noise.
    // Compared case-insensitively: the wire name varies ("hello", "isMaster", "ismaster").
    private static readonly FrozenSet<string> IgnoredCommands =
        FrozenSet.ToFrozenSet<string>(["hello", "ismaster"], StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, MongoOperationType> OperationTypes =
        new Dictionary<string, MongoOperationType>(StringComparer.OrdinalIgnoreCase)
        {
            ["insert"] = MongoOperationType.Insert,
            ["delete"] = MongoOperationType.Delete,
            ["find"] = MongoOperationType.Find,
            ["update"] = MongoOperationType.Update,
            ["aggregate"] = MongoOperationType.Aggregate,
            ["count"] = MongoOperationType.Count,
            ["distinct"] = MongoOperationType.Distinct,
            ["mapreduce"] = MongoOperationType.MapReduce,
            ["createindexes"] = MongoOperationType.CreateIndex,
            ["dropindexes"] = MongoOperationType.DropIndex,
            ["create"] = MongoOperationType.CreateCollection,
            ["drop"] = MongoOperationType.DropCollection,
            ["listcollections"] = MongoOperationType.ListCollections,
            ["listindexes"] = MongoOperationType.ListIndexes,
            ["findandmodify"] = MongoOperationType.FindAndModify,
            ["bulkwrite"] = MongoOperationType.BulkWrite,
            ["getmore"] = MongoOperationType.GetMore,
            ["killcursors"] = MongoOperationType.KillCursors,
            ["renameCollection"] = MongoOperationType.RenameCollection,
            ["copydb"] = MongoOperationType.CopyDb,
            ["collMod"] = MongoOperationType.CollMod,
            ["dropDatabase"] = MongoOperationType.DropDatabase,
            ["explain"] = MongoOperationType.Explain,
            ["group"] = MongoOperationType.Group,
            ["geoNear"] = MongoOperationType.GeoNear,
            ["geoSearch"] = MongoOperationType.GeoSearch,
            ["getLastError"] = MongoOperationType.GetLastError,
            ["getPrevError"] = MongoOperationType.GetPrevError,
            ["isMaster"] = MongoOperationType.IsMaster,
            ["listDatabases"] = MongoOperationType.ListDatabases,
            ["reIndex"] = MongoOperationType.ReIndex,
            ["replSetGetStatus"] = MongoOperationType.ReplSetGetStatus,
            ["serverStatus"] = MongoOperationType.ServerStatus,
            ["shardConnPoolStats"] = MongoOperationType.ShardConnPoolStats,
            ["whatsmyuri"] = MongoOperationType.WhatsMyUri,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    static MongoInstrumentation()
    {
        MetricProviderRegistrar.RegisterAll();
    }

    /// <summary>
    /// Instruments the given MongoClientSettings for Prometheus metrics.
    /// </summary>
    /// <param name="settings">The MongoClientSettings to instrument.</param>
    /// <param name="configurator">A delegate to do additional writing for events if needed</param>
    /// <returns>The instrumented MongoClientSettings.</returns>
    /// <exception cref="Exception"><see cref="ClusterBuilder"/> delegate in ClusterConfigurator throws an exception</exception>
    public static MongoClientSettings InstrumentForPrometheus(this MongoClientSettings settings, Action<ClusterBuilder>? configurator = null)
    {
        var existingConfigurator = settings.ClusterConfigurator;
        settings.ClusterConfigurator = cb =>
        {
            existingConfigurator?.Invoke(cb);

            configurator?.Invoke(cb);

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
            Endpoint = @event.ServerId.EndPoint.ToString() ?? string.Empty,
            ClusterId = @event.ServerId.ClusterId.Value,
            ConnectionId = @event.ConnectionId,
        };

        EventHub.Default.Publish(connectionEvent);
    }

    private static void OnConnectionFailed(ConnectionFailedEvent @event)
    {
        var connectionEvent = new MongoConnectionFailedEvent
        {
            Endpoint = @event.ServerId.EndPoint.ToString() ?? string.Empty,
            Exception = @event.Exception,
            ClusterId = @event.ServerId.ClusterId.Value,
            ConnectionId = @event.ConnectionId,
        };

        EventHub.Default.Publish(connectionEvent);
    }

    private static void OnConnectionOpened(ConnectionOpenedEvent @event)
    {
        var connectionEvent = new MongoConnectionOpenedEvent
        {
            Endpoint = @event.ServerId.EndPoint.ToString() ?? string.Empty,
            ClusterId = @event.ServerId.ClusterId.Value,
            ConnectionId = @event.ConnectionId,
        };

        EventHub.Default.Publish(connectionEvent);
    }

    private static void OnCommandFailed(CommandFailedEvent e)
    {
        if (IgnoredCommands.Contains(e.CommandName))
        {
            return;
        }

        if (!Commands.TryRemove((e.ConnectionId, e.RequestId), out var correlation))
        {
            return;
        }

        if (correlation.Collection.Length == 0)
        {
            return;
        }

        // Note: the start command document is NOT retained. The driver recycles its
        // buffers after sending, so touching it here would throw ObjectDisposedException.
        // End events therefore carry scalars only; document views are start-event-only.
        var commandEvent = EventPool<MongoCommandEventFailure>.Rent();
        try
        {
            commandEvent.RequestId = e.RequestId;
            commandEvent.OperationId = e.OperationId ?? 0;
            commandEvent.OperationRawType = e.CommandName;
            commandEvent.RawRequestSizeInBytes = correlation.RequestSizeInBytes;
            commandEvent.Duration = e.Duration;
            commandEvent.Failure = e.Failure;
            commandEvent.OperationType = correlation.OperationType;
            commandEvent.TargetDatabase = correlation.Database;
            commandEvent.TargetCollection = correlation.Collection;
            commandEvent.CursorId = correlation.CursorId;
            EventHub.Default.Publish(commandEvent);
        }
        finally
        {
            EventPool<MongoCommandEventFailure>.Return(commandEvent);
        }
    }

    private static void OnCommandSucceeded(CommandSucceededEvent e)
    {
        if (IgnoredCommands.Contains(e.CommandName))
        {
            return;
        }

        if (!Commands.TryRemove((e.ConnectionId, e.RequestId), out var correlation))
        {
            return;
        }

        if (correlation.Collection.Length == 0)
        {
            return;
        }

        var reply = e.Reply;
        var commandEvent = EventPool<MongoCommandEventSuccess>.Rent();
        try
        {
            commandEvent.RequestId = e.RequestId;
            commandEvent.OperationId = e.OperationId ?? 0;
            commandEvent.OperationRawType = e.CommandName;
            commandEvent.RawRequestSizeInBytes = correlation.RequestSizeInBytes;
            commandEvent.Duration = e.Duration;
            commandEvent.OperationType = correlation.OperationType;
            commandEvent.TargetDatabase = correlation.Database;
            commandEvent.TargetCollection = correlation.Collection;
            commandEvent.CursorId = correlation.CursorId;
            commandEvent.ReplyDocument = reply;

            ExtractCursorInfo(reply, commandEvent);
            EventHub.Default.Publish(commandEvent);
        }
        finally
        {
            EventPool<MongoCommandEventSuccess>.Return(commandEvent);
        }
    }

    private static void OnCommandStarted(CommandStartedEvent e)
    {
        if (IgnoredCommands.Contains(e.CommandName))
        {
            return;
        }

        var command = e.Command;
        var targetCollection = GetCollection(e.CommandName, command);

        // Commands without a target collection (ping, auth, endSessions, ...) publish nothing,
        // and their end events bail out on the same check, so they need neither a correlation
        // entry nor the (O(command size)) serialization below.
        if (targetCollection.Length == 0)
        {
            return;
        }

        var operationType = GetOperationType(e.CommandName);
        var database = e.DatabaseNamespace?.DatabaseName ?? GetDatabase(command);

        // The exact wire size is observed by counting serialized bytes; no buffer
        // is ever materialized (see BsonSizeCounter).
        var rawCommandSizeInBytes = BsonSizeCounter.GetSizeInBytes(command);

        var key = (e.ConnectionId, e.RequestId);
        var correlation = new CommandCorrelation(
            database,
            targetCollection,
            operationType,
            rawCommandSizeInBytes,
            GetCursorId(e.CommandName, command));

        // Last-writer-wins: a stale orphan under the same key must never shadow a live command.
        Commands[key] = correlation;

        var commandEvent = EventPool<MongoCommandEventStart>.Rent();
        try
        {
            commandEvent.RequestId = e.RequestId;
            commandEvent.OperationId = e.OperationId ?? 0;
            commandEvent.OperationRawType = e.CommandName;
            commandEvent.CommandDocument = command;
            commandEvent.RawRequestSizeInBytes = rawCommandSizeInBytes;
            commandEvent.Duration = null; // no duration yet
            commandEvent.OperationType = operationType;
            commandEvent.TargetDatabase = database;
            commandEvent.TargetCollection = targetCollection;
            commandEvent.FilterDocument = GetFilterDocument(command, operationType);
            commandEvent.CursorId = correlation.CursorId;
            commandEvent.KilledCursorIds = GetKilledCursorIds(e.CommandName, command);
            EventHub.Default.Publish(commandEvent);
        }
        finally
        {
            EventPool<MongoCommandEventStart>.Return(commandEvent);
        }
    }

    private static MongoOperationType GetOperationType(string commandName) =>
        OperationTypes.TryGetValue(commandName, out var operationType)
            ? operationType
            : MongoOperationType.Other;

    internal static string GetCollection(string commandName, BsonDocument command)
    {
        if (command.TryGetValue("collection", out var collection) &&
            collection is BsonString collectionName)
        {
            return collectionName.AsString;
        }

        // Bulk writes carry no top-level collection: the command value is just `1`
        // (`{ bulkWrite: 1, nsInfo: [{ ns: "db.coll" }], ... }`). Attribute the command
        // to the first listed namespace; multi-namespace bulks are rare and share one
        // request-size observation, so the first entry is the honest label.
        // The namespace is "db.collection" with the split on the FIRST dot, because
        // collection names may legally contain dots themselves.
        if (string.Equals(commandName, "bulkWrite", StringComparison.OrdinalIgnoreCase) &&
            command.TryGetValue("nsInfo", out var nsInfo) &&
            nsInfo is BsonArray { Count: > 0 } namespaces &&
            namespaces[0] is BsonDocument firstNamespace &&
            firstNamespace.TryGetValue("ns", out var ns) &&
            ns is BsonString nsString)
        {
            var namespaceValue = nsString.AsString;
            var dot = namespaceValue.IndexOf('.');
            if (dot >= 0 && dot < namespaceValue.Length - 1)
            {
                return namespaceValue[(dot + 1)..];
            }

            return string.Empty;
        }

        if (command.TryGetValue(commandName, out var collectionNameValue) &&
            collectionNameValue is BsonString collectionNameFromCommand)
        {
            return collectionNameFromCommand.AsString;
        }

        return string.Empty;
    }

    // Only getMore carries a cursor id as its command value (`getMore: <int64>`). For every other
    // command that value is the collection name, which must never be read as an id.
    internal static long? GetCursorId(string commandName, BsonDocument command) =>
        OperationTypes.TryGetValue(commandName, out var type) &&
        type == MongoOperationType.GetMore &&
        command.TryGetValue(commandName, out var value)
            ? value switch
            {
                BsonInt64 int64 => int64.AsInt64,
                BsonInt32 int32 => int32.AsInt32,
                _ => null,
            }
            : null;

    // Cursor ids named by a killCursors command (`{ killCursors: "coll", cursors: [...] }`).
    // A killCursors is a separate driver operation with its own operation id, so cursor
    // providers match it to the abandoned cursor by these ids, never by operation id.
    internal static List<long>? GetKilledCursorIds(string commandName, BsonDocument command)
    {
        if (!OperationTypes.TryGetValue(commandName, out var type) ||
            type != MongoOperationType.KillCursors ||
            !command.TryGetValue("cursors", out var cursors) ||
            cursors is not BsonArray cursorArray ||
            cursorArray.Count == 0)
        {
            return null;
        }

        List<long>? ids = null;
        foreach (var cursor in cursorArray)
        {
            long id = cursor switch
            {
                BsonInt64 int64 => int64.AsInt64,
                BsonInt32 int32 => int32.AsInt32,
                _ => 0,
            };

            if (id != 0)
            {
                ids ??= new List<long>(cursorArray.Count);
                ids.Add(id);
            }
        }

        return ids;
    }

    internal static BsonDocument? GetFilterDocument(BsonDocument command, MongoOperationType operationType)
    {
        if (command.TryGetValue("filter", out var filter) && filter is BsonDocument filterDocument)
        {
            return filterDocument;
        }

        // Aggregates carry no "filter": their complexity lives in "pipeline".
        // Wrap the array (stages are referenced, never copied) so the same
        // leaf-counting walk measures stage clauses with identical semantics.
        if (operationType == MongoOperationType.Aggregate &&
            command.TryGetValue("pipeline", out var pipeline) && pipeline is BsonArray pipelineArray)
        {
            return new BsonDocument("pipeline", pipelineArray);
        }

        return null;
    }

    private static void ExtractCursorInfo(BsonDocument? reply, MongoCommandEvent target)
    {
        if (reply is null || !reply.TryGetValue("cursor", out var cursorValue) ||
            cursorValue is not BsonDocument cursor)
        {
            return;
        }

        if (cursor.TryGetValue("id", out var idValue))
        {
            var replyCursorId = idValue switch
            {
                BsonInt64 int64 => (long?)int64.AsInt64,
                BsonInt32 int32 => (long?)int32.AsInt32,
                _ => null,
            };
            if (replyCursorId.HasValue)
            {
                target.IsFinalBatch = replyCursorId.Value == 0;
                target.CursorId ??= replyCursorId;
            }
        }

        if (cursor.TryGetValue("firstBatch", out var firstBatch) && firstBatch is BsonArray firstBatchArray)
        {
            target.IsFirstBatch = true;
            target.BatchDocumentCount = firstBatchArray.Count;
        }
        else if (cursor.TryGetValue("nextBatch", out var nextBatch) && nextBatch is BsonArray nextBatchArray)
        {
            target.BatchDocumentCount = nextBatchArray.Count;
        }
    }

    private static string GetDatabase(BsonDocument command) =>
        command.TryGetValue("$db", out var database) && database is BsonString databaseName
            ? databaseName.AsString
            : "no database";
}
