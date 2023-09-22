namespace PrometheusNet.MongoDb;

/// <summary>
/// Enum representing MongoDB operation types.
/// </summary>
public enum MongoOperationType
{
    Insert,
    Delete,
    Find,
    Update,
    Aggregate,
    Count,
    Distinct,
    MapReduce,
    CreateIndex,
    DropIndex,
    CreateCollection,
    DropCollection,
    ListCollections,
    ListIndexes,
    FindAndModify,
    BulkWrite,
    GetMore,
    KillCursors,
    RenameCollection,
    CopyDb,
    CollMod,
    DropDatabase,
    Explain,
    Group,
    GeoNear,
    GeoSearch,
    GetLastError,
    GetPrevError,
    IsMaster,
    ListDatabases,
    ReIndex,
    ReplSetGetStatus,
    ServerStatus,
    ShardConnPoolStats,
    WhatsMyUri,
    Other
}