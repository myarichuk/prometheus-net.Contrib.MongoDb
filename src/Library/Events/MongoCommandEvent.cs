using MongoDB.Bson;

namespace PrometheusNet.MongoDb.Events;

/// <summary>
/// Base class for MongoDB command lifecycle events.
/// Scalar routing fields (database, collection, operation type, cursor bookkeeping)
/// are extracted once from the driver's <see cref="BsonDocument"/> by the
/// instrumentation and published allocation-free to metric providers.
/// The legacy <see cref="Command"/> / <see cref="Reply"/> dictionary views are
/// still available for custom providers but are materialized lazily, only when read.
/// <remarks>
/// Document references (<see cref="CommandDocument"/>, <see cref="FilterDocument"/>,
/// <see cref="MongoCommandEventSuccess.ReplyDocument"/>) borrow the driver's buffers and
/// are valid only while the event is being dispatched. The driver recycles those buffers
/// afterwards, so handlers must not retain them (or the events) beyond the callback:
/// touching them later throws <see cref="ObjectDisposedException"/>. Success and failure
/// events therefore expose scalars only; <see cref="CommandDocument"/> is populated on
/// start events alone.
/// </remarks>
/// </summary>
public abstract class MongoCommandEvent
{
    public int RawRequestSizeInBytes { get; set; }

    public string OperationRawType { get; set; } = string.Empty;

    public long OperationId { get; set; }

    public int RequestId { get; set; }

    public MongoOperationType OperationType { get; set; }

    public string TargetDatabase { get; set; } = string.Empty;

    public string TargetCollection { get; set; } = string.Empty;

    /// <summary>
    /// The command filter document, when the command carries one (e.g. <c>find</c>).
    /// This is a reference into the driver's event data; do not mutate it.
    /// </summary>
    public BsonDocument? FilterDocument { get; set; }

    /// <summary>
    /// Number of documents in this reply's cursor batch (<c>firstBatch</c>/<c>nextBatch</c>),
    /// or <c>null</c> when the reply carries no batch.
    /// </summary>
    public int? BatchDocumentCount { get; set; }

    /// <summary>Whether the reply opens a cursor (contains <c>firstBatch</c>).</summary>
    public bool IsFirstBatch { get; set; }

    /// <summary>Whether the reply closes a cursor (cursor <c>id</c> is 0).</summary>
    public bool IsFinalBatch { get; set; }

    public TimeSpan? Duration { get; set; }

    public long? CursorId { get; set; }

    /// <summary>
    /// Clears every field so a pooled instance can be reused without leaking
    /// state (or references) from the previous command. Keep in sync with the
    /// properties above: a field added here but not reset here is a corruption bug.
    /// </summary>
    internal virtual void Reset()
    {
        RawRequestSizeInBytes = 0;
        OperationRawType = string.Empty;
        OperationId = 0;
        RequestId = 0;
        OperationType = MongoOperationType.Other;
        TargetDatabase = string.Empty;
        TargetCollection = string.Empty;
        FilterDocument = null;
        BatchDocumentCount = null;
        IsFirstBatch = false;
        IsFinalBatch = false;
        Duration = null;
        CursorId = null;
        CommandDocument = null;
    }

    private BsonDocument? _commandDocument;

    private Dictionary<string, object>? _command;

    /// <summary>
    /// Legacy full-command view, materialized from <see cref="CommandDocument"/>
    /// on first read. Prefer <see cref="FilterDocument"/> and the scalar routing
    /// properties on the hot path; converting the whole command is expensive.
    /// </summary>
    public Dictionary<string, object>? Command
    {
        get => _command ??= _commandDocument?.ToDictionary();
        set
        {
            _command = value;
            _commandDocument = null;
        }
    }

    /// <summary>
    /// The raw driver command document. Internal hot-path access; do not mutate it.
    /// </summary>
    internal BsonDocument? CommandDocument
    {
        get => _commandDocument;
        set
        {
            _commandDocument = value;
            _command = null;
        }
    }
}
