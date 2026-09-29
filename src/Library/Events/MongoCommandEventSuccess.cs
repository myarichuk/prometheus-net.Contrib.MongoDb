using MongoDB.Bson;

namespace PrometheusNet.MongoDb.Events;

public class MongoCommandEventSuccess : MongoCommandEvent
{
    internal override void Reset()
    {
        base.Reset();
        ReplyDocument = null;
    }

    private BsonDocument? _replyDocument;

    private Dictionary<string, object>? _reply;

    private byte[]? _rawReply;

    /// <summary>
    /// The raw driver reply document. Internal hot-path access; do not mutate it.
    /// </summary>
    internal BsonDocument? ReplyDocument
    {
        get => _replyDocument;
        set
        {
            _replyDocument = value;
            _reply = null;
            _rawReply = null;
        }
    }

    /// <summary>
    /// Exact reply size in bytes. Serialized from the reply document on first read
    /// and cached; reading it is O(reply size), so avoid it unless needed.
    /// </summary>
    public byte[] RawReply
    {
        get => _rawReply ??= _replyDocument?.ToBson() ?? [];
        set => _rawReply = value;
    }

    /// <summary>
    /// Legacy full-reply view, materialized from <see cref="ReplyDocument"/>
    /// on first read. Prefer <see cref="MongoCommandEvent.BatchDocumentCount"/>,
    /// <see cref="MongoCommandEvent.IsFirstBatch"/> and
    /// <see cref="MongoCommandEvent.IsFinalBatch"/> on the hot path; converting a
    /// full result batch is the most expensive thing this library used to do.
    /// </summary>
    public Dictionary<string, object>? Reply
    {
        get => _reply ??= _replyDocument?.ToDictionary();
        set
        {
            _reply = value;
            _replyDocument = null;
        }
    }
}
