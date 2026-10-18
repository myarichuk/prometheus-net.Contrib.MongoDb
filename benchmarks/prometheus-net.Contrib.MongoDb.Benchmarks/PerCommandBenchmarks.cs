using System.Diagnostics.Metrics;
using System.Net;
using BenchmarkDotNet.Attributes;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Events;
using MongoDB.Driver.Core.Servers;
using Prometheus;
using PrometheusNet.Contrib.MongoDb;

namespace PrometheusNet.MongoDb.Benchmarks;

/// <summary>
/// The instrumentation's whole cost for one command (start and success events through every
/// provider), with no driver or network around it, per configuration. The difference to
/// <see cref="Default"/> is what an option costs.
/// </summary>
[MemoryDiagnoser]
public class PerCommandBenchmarks
{
    private static readonly ConnectionId Connection =
        new(new ServerId(new ClusterId(1), new DnsEndPoint("bench", 27017)), 1);

    private static readonly DatabaseNamespace Database = new("benchdb");

    private readonly Meter _meter = new("bench");

    private byte[] _command = null!;

    private byte[] _reply = null!;

    private InstrumentationSession _default = null!;

    private InstrumentationSession _noSizes = null!;

    private InstrumentationSession _normalized = null!;

    private InstrumentationSession _overflowing = null!;

    private InstrumentationSession _withMeter = null!;

    private int _requestId;

    [GlobalSetup]
    public void Setup()
    {
        _command = new BsonDocument
        {
            { "find", "bench" },
            { "filter", new BsonDocument("name", "doc-42") },
            { "$db", "benchdb" },
        }.ToBson();
        _reply = new BsonDocument
        {
            { "cursor", new BsonDocument { { "firstBatch", new BsonArray { new BsonDocument("_id", 1) } }, { "id", 0L }, { "ns", "benchdb.bench" } } },
            { "ok", 1.0 },
        }.ToBson();

        _default = Session(_ => { });
        _noSizes = Session(o => o.MeasureSizes = false);
        _normalized = Session(o => o.NormalizeLabels = static (db, coll) => (db.StartsWith("tenant_", StringComparison.Ordinal) ? "tenant_*" : db, coll));
        _overflowing = Session(o => o.MaxCollectionLabels = 1);
        _withMeter = Session(o => o.Meter = _meter);

        // Fill the overflowing session's only slot, so "bench" always overflows.
        _overflowing.OnCommandStarted(new CommandStartedEvent("count", new BsonDocument("count", "other"), Database, 1, -1, Connection));
        _overflowing.OnCommandSucceeded(new CommandSucceededEvent("count", new BsonDocument("ok", 1), Database, 1, -1, Connection, TimeSpan.FromMilliseconds(1)));

        // A Meter only records while someone listens.
        var listener = new MeterListener { InstrumentPublished = (i, l) => { if (i.Meter == _meter) l.EnableMeasurementEvents(i); } };
        listener.SetMeasurementEventCallback<double>(static (_, _, _, _) => { });
        listener.Start();
    }

    /// <summary>Building and disposing the documents and events alone: subtract it from the others.</summary>
    [Benchmark]
    public void EventsOnly() => Run(null);

    [Benchmark(Baseline = true)]
    public void Default() => Run(_default);

    [Benchmark]
    public void NoSizes() => Run(_noSizes);

    [Benchmark]
    public void Normalized() => Run(_normalized);

    [Benchmark]
    public void OverflowingLabels() => Run(_overflowing);

    [Benchmark]
    public void PrometheusAndMeter() => Run(_withMeter);

    private static InstrumentationSession Session(Action<MongoInstrumentationOptions> configure)
    {
        var options = new MongoInstrumentationOptions { Registry = Metrics.NewCustomRegistry() };
        configure(options);
        return new InstrumentationSession(options);
    }

    // Fresh documents per command, disposed afterwards, as the driver does: a wire document
    // keeps every nested document read from it until it is disposed.
    private void Run(InstrumentationSession? session)
    {
        var requestId = ++_requestId;
        using (var command = new RawBsonDocument(_command))
        {
            var started = new CommandStartedEvent("find", command, Database, 7, requestId, Connection);
            session?.OnCommandStarted(started);
        }

        using (var reply = new RawBsonDocument(_reply))
        {
            var succeeded = new CommandSucceededEvent("find", reply, Database, 7, requestId, Connection, TimeSpan.FromMilliseconds(1));
            session?.OnCommandSucceeded(succeeded);
        }
    }
}
