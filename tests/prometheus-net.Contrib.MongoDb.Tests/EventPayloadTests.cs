using MongoDB.Bson;
using PrometheusNet.MongoDb.Events;

namespace PrometheusNet.MongoDb.Tests;

/// <summary>
/// The legacy <c>Dictionary</c> / <c>byte[]</c> views on command events are
/// materialized lazily from the backing <see cref="BsonDocument"/>: built-in
/// providers never touch them, so per-command conversions must not happen
/// unless a custom provider actually reads them.
/// </summary>
public class EventPayloadTests
{
    [Fact]
    public void Start_event_materializes_command_dictionary_on_demand()
    {
        var command = new BsonDocument
        {
            { "find", "things" },
            { "filter", new BsonDocument("name", "a") },
            { "$db", "test" },
        };

        var @event = new MongoCommandEventStart { CommandDocument = command };

        Assert.Same(command, @event.CommandDocument);
        var dictionary = @event.Command;
        Assert.NotNull(dictionary);
        Assert.Equal("things", dictionary["find"]);
    }

    [Fact]
    public void Success_event_materializes_reply_views_on_demand()
    {
        var reply = new BsonDocument
        {
            { "ok", 1 },
            {
                "cursor",
                new BsonDocument
                {
                    { "id", 0L },
                    { "firstBatch", new BsonArray { new BsonDocument("a", 1) } },
                }
            },
        };

        var @event = new MongoCommandEventSuccess { ReplyDocument = reply };

        var raw = @event.RawReply;
        Assert.NotNull(raw);
        Assert.True(raw.Length > 0);

        var dictionary = @event.Reply;
        Assert.NotNull(dictionary);
        Assert.True(dictionary.ContainsKey("cursor"));
    }

    [Fact]
    public void Failure_event_keeps_command_view_available()
    {
        var command = new BsonDocument { { "insert", "things" }, { "$db", "test" } };

        var @event = new MongoCommandEventFailure
        {
            CommandDocument = command,
            Failure = new InvalidOperationException("boom"),
        };

        Assert.NotNull(@event.Command);
        Assert.Equal("things", @event.Command!["insert"]);
    }
}
