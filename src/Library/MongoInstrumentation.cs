using System.Collections.Frozen;
using MongoDB.Bson;
using MongoDB.Driver;
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
/// Instruments MongoDB clients: <see cref="InstrumentForPrometheus"/> attaches a separate
/// instrumentation to each client's settings. The command parsing shared by all of them
/// (what a command is, which collection it targets, what its reply says about cursors) is
/// stateless and lives here.
/// </summary>
public static class MongoInstrumentation
{
    // Commands the driver sends on its own: handshakes and heartbeats, authentication and
    // session cleanup. The application never asked for them, so they would only add noise.
    // Every other command is measured, with or without a collection.
    // Compared case-insensitively: the wire name varies ("hello", "isMaster", "ismaster").
    private static readonly FrozenSet<string> IgnoredCommands =
        FrozenSet.ToFrozenSet<string>(
            ["hello", "ismaster", "saslStart", "saslContinue", "authenticate", "endSessions"],
            StringComparer.OrdinalIgnoreCase);

    // The namespace a server gives cursors opened by database-level commands
    // (`aggregate: 1` reports `$cmd.aggregate`, `listCollections` reports `$cmd.listCollections`).
    private const string CommandNamespacePrefix = "$cmd.";

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

    /// <summary>
    /// Instruments the client built from these settings. Each call creates a separate
    /// instrumentation with its own options; clients sharing a registry share its metrics.
    /// </summary>
    /// <param name="settings">The settings to instrument; they are changed and returned.</param>
    /// <param name="configure">Configures the instrumentation (registry, Meter, labels, ...).</param>
    /// <returns>The instrumented settings.</returns>
    public static MongoClientSettings InstrumentForPrometheus(this MongoClientSettings settings, Action<MongoInstrumentationOptions>? configure = null)
    {
        Instrument(settings, configure);
        return settings;
    }

    /// <summary>Instruments the settings and returns the instrumentation, for tests.</summary>
    internal static InstrumentationSession Instrument(MongoClientSettings settings, Action<MongoInstrumentationOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var options = new MongoInstrumentationOptions();
        configure?.Invoke(options);
        var session = new InstrumentationSession(options);

        var existingConfigurator = settings.ClusterConfigurator;
        settings.ClusterConfigurator = cb =>
        {
            existingConfigurator?.Invoke(cb);
            session.Attach(cb);
        };

        return session;
    }

    internal static bool IsIgnored(string commandName) => IgnoredCommands.Contains(commandName);

    internal static MongoOperationType GetOperationType(string commandName) =>
        OperationTypes.TryGetValue(commandName, out var operationType)
            ? operationType
            : MongoOperationType.Other;

    internal static string GetCollection(string commandName, BsonDocument command) =>
        GetCollection(commandName, GetOperationType(commandName), command);

    internal static string GetCollection(string commandName, MongoOperationType operationType, BsonDocument command)
    {
        // getMore's command value is the cursor id; its collection is a separate field.
        // Only getMore looks for it: on any other command the lookup is a miss, and a miss
        // reads the whole wire-encoded command.
        if (operationType == MongoOperationType.GetMore)
        {
            if (command is RawBsonDocument rawGetMore && RawBson.TryReadString(rawGetMore.Slice, "collection"u8, out var rawCollection))
            {
                return rawCollection is null ? string.Empty : CollectionLabel(rawCollection);
            }

            return command.TryGetValue("collection", out var collection) && collection is BsonString collectionName
                ? CollectionLabel(collectionName.AsString)
                : string.Empty;
        }

        // Bulk writes carry no top-level collection: the command value is just `1`
        // (`{ bulkWrite: 1, nsInfo: [{ ns: "db.coll" }], ... }`). Attribute the command
        // to the first listed namespace; multi-namespace bulks are rare and share one
        // request-size observation, so the first entry is the honest label.
        // The namespace is "db.collection" with the split on the FIRST dot, because
        // collection names may legally contain dots themselves.
        if (operationType == MongoOperationType.BulkWrite &&
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

        // The command's own value is its first element. On a wire-encoded command the driver's
        // lookup builds a reader per call; the raw walk allocates nothing for a known name.
        if (command is RawBsonDocument raw && RawBson.TryReadString(raw.Slice, default, out var rawName))
        {
            return rawName is null ? string.Empty : CollectionLabel(rawName);
        }

        if (command.TryGetValue(commandName, out var collectionNameValue) &&
            collectionNameValue is BsonString collectionNameFromCommand)
        {
            return CollectionLabel(collectionNameFromCommand.AsString);
        }

        return string.Empty;
    }

    // A cursor opened by a database-level command is labelled "" when it opens, so its
    // getMore and killCursors (which name `$cmd.<command>`) must close it under "" too.
    private static string CollectionLabel(string collection) =>
        collection.StartsWith(CommandNamespacePrefix, StringComparison.Ordinal) ? string.Empty : collection;

    // Only getMore carries a cursor id as its command value (`getMore: <int64>`). For every other
    // command that value is the collection name, which must never be read as an id.
    internal static long? GetCursorId(string commandName, BsonDocument command)
    {
        if (!OperationTypes.TryGetValue(commandName, out var type) || type != MongoOperationType.GetMore)
        {
            return null;
        }

        if (command is RawBsonDocument raw && RawBson.TryReadInt64(raw.Slice, default, out var rawId))
        {
            return rawId;
        }

        return command.TryGetValue(commandName, out var value)
            ? value switch
            {
                BsonInt64 int64 => int64.AsInt64,
                BsonInt32 int32 => int32.AsInt32,
                _ => null,
            }
            : null;
    }

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
        // Only find's "filter" is a query filter worth measuring (listCollections and
        // listDatabases carry one too, over metadata); probing every insert/update for one
        // would read each command to its end for nothing.
        if (operationType == MongoOperationType.Find)
        {
            return command.TryGetValue("filter", out var filter) && filter is BsonDocument filterDocument
                ? filterDocument
                : null;
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

    // Cursors the server never reaps when idle, so an idle one is not abandoned: find with
    // noCursorTimeout or tailable (tailable cursors wait for new documents), and change streams.
    internal static bool CursorNeverTimesOut(BsonDocument command, MongoOperationType operationType, BsonDocument? filterDocument)
    {
        if (operationType == MongoOperationType.Find)
        {
            if (command is RawBsonDocument raw &&
                RawBson.TryReadFindCursorFlags(raw.Slice, out var rawNoTimeout, out var rawTailable))
            {
                return rawNoTimeout || rawTailable;
            }

            return IsTrue(command, "noCursorTimeout") || IsTrue(command, "tailable");
        }

        if (operationType != MongoOperationType.Aggregate)
        {
            return false;
        }

        // A change stream is an aggregate whose first stage is $changeStream.
        if (command is RawBsonDocument rawAggregate && RawBson.TryReadIsChangeStream(rawAggregate.Slice, out var isChangeStream))
        {
            return isChangeStream;
        }

        filterDocument ??= GetFilterDocument(command, operationType);
        return filterDocument is not null &&
               filterDocument.TryGetValue("pipeline", out var pipeline) &&
               pipeline is BsonArray { Count: > 0 } stages &&
               stages[0] is BsonDocument { ElementCount: > 0 } firstStage &&
               firstStage.GetElement(0).Name == "$changeStream";

        static bool IsTrue(BsonDocument document, string name) =>
            document.TryGetValue(name, out var value) && value is BsonBoolean { Value: true };
    }

    internal static void ExtractCursorInfo(BsonDocument? reply, MongoCommandEvent target)
    {
        if (reply is RawBsonDocument raw &&
            RawBson.TryReadCursor(raw.Slice, out var hasCursor, out var cursorId, out var isFirstBatch, out var batchCount))
        {
            if (hasCursor)
            {
                ApplyCursorInfo(target, cursorId, isFirstBatch, batchCount);
            }

            return;
        }

        ExtractCursorInfoSlow(reply, target);
    }

    private static void ApplyCursorInfo(MongoCommandEvent target, long? replyCursorId, bool isFirstBatch, int? batchCount)
    {
        if (replyCursorId.HasValue)
        {
            target.IsFinalBatch = replyCursorId.Value == 0;
            target.CursorId ??= replyCursorId;
        }

        target.IsFirstBatch = isFirstBatch;
        target.BatchDocumentCount = batchCount;
    }

    // Materialized replies, and wire-encoded ones the raw walker declined.
    internal static void ExtractCursorInfoSlow(BsonDocument? reply, MongoCommandEvent target)
    {
        if (reply is null || !reply.TryGetValue("cursor", out var cursorValue) ||
            cursorValue is not BsonDocument cursor)
        {
            return;
        }

        long? replyCursorId = null;
        var isFirstBatch = false;
        int? batchCount = null;
        foreach (var element in cursor)
        {
            switch (element.Name)
            {
                case "id":
                    replyCursorId = element.Value switch
                    {
                        BsonInt64 int64 => int64.AsInt64,
                        BsonInt32 int32 => int32.AsInt32,
                        _ => replyCursorId,
                    };
                    break;
                case "firstBatch" when element.Value is BsonArray firstBatch:
                    isFirstBatch = true;
                    batchCount = firstBatch.Count;
                    break;
                case "nextBatch" when element.Value is BsonArray nextBatch && !isFirstBatch:
                    batchCount = nextBatch.Count;
                    break;
            }
        }

        ApplyCursorInfo(target, replyCursorId, isFirstBatch, batchCount);
    }

    internal static string GetDatabase(BsonDocument command) =>
        command.TryGetValue("$db", out var database) && database is BsonString databaseName
            ? databaseName.AsString
            : "no database";
}
