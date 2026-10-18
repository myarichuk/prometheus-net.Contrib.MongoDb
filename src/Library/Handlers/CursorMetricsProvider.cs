using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using MongoDB.Driver;
using Prometheus;
using PrometheusNet.MongoDb;
using PrometheusNet.MongoDb.Events;
using PrometheusNet.MongoDb.Handlers;

namespace PrometheusNet.Contrib.MongoDb.Handlers;

/// <summary>
/// Follows every server cursor from the batch that opens it to the one event that ends it,
/// and publishes all cursor metrics from that single place: the open-cursor gauge, the
/// cursor lifetime, the documents fetched, and why the cursor closed.
/// <para/>
/// A cursor is open when a first batch comes back with a non-zero id, whatever command
/// opened it (find, aggregate, listIndexes, ...). It closes exactly once, for one reason:
/// <list type="bullet">
/// <item><c>exhausted</c>: a <c>getMore</c> returned the last batch.</item>
/// <item><c>killed</c>: the application disposed it early and the driver sent <c>killCursors</c>.</item>
/// <item><c>server_expired</c>: a <c>getMore</c> failed with CursorNotFound, usually because
/// the server reaped the cursor while the consumer was too slow.</item>
/// <item><c>error</c>: a <c>getMore</c> failed for any other reason.</item>
/// <item><c>abandoned</c>: no batch was fetched for longer than <see cref="IdleTimeout"/> and the
/// cursor was never killed, so the application leaked it and the server will reap it.</item>
/// </list>
/// Abandoned cursors are found by a sweep that runs each time metrics are collected, so no
/// timer thread is needed and expiry is evaluated exactly when someone looks.
/// </summary>
internal sealed class CursorMetricsProvider : IMongoDbClientMetricProvider, ISweepingProvider
{
    internal const string Exhausted = "exhausted";
    internal const string Killed = "killed";
    internal const string ServerExpired = "server_expired";
    internal const string Error = "error";
    internal const string Abandoned = "abandoned";

    private const int CursorNotFoundCode = 43;

    // 1 ms to 70 min: a cursor read in one go lives milliseconds, an abandoned one until
    // the server reaps it after its 10-minute idle timeout.
    private static readonly double[] LifetimeBuckets = Histogram.ExponentialBuckets(0.001, 4, 12);

    // 1 to 262,144 documents.
    private static readonly double[] DocumentBuckets = Histogram.ExponentialBuckets(1, 4, 10);

    // Cursor ids are unique per server, not globally.
    private readonly ConcurrentDictionary<(EndPoint? Server, long CursorId), CursorState> _cursors = new();

    private readonly MetricChildCache<(string, string), GaugeChild> _openCursorsCache;
    private readonly MetricChildCache<(string, string), HistogramChild> _lifetimeCache;
    private readonly MetricChildCache<(string, string), HistogramChild> _documentsCache;
    private readonly MetricChildCache<(string, string, string), CounterChild> _closedCache;

    public CursorMetricsProvider(InstrumentationSession session)
    {
        var sink = session.Sink;
        OpenCursors = sink.UpDownGauge(
            "mongodb_client_cursors_open",
            "Server cursors open: opened by a first batch and not yet closed",
            "target_collection",
            "target_db");
        CursorLifetime = sink.Histogram(
            "mongodb_client_cursor_lifetime_seconds",
            "How long server cursors stayed open, from the first batch to the close (seconds)",
            LifetimeBuckets,
            "target_collection",
            "target_db");
        CursorDocuments = sink.Histogram(
            "mongodb_client_cursor_documents",
            "Documents fetched per cursor over all of its batches; a single-batch reply counts as one cursor",
            DocumentBuckets,
            "target_collection",
            "target_db");
        CursorsClosed = sink.Counter(
            "mongodb_client_cursors_closed_total",
            "Cursors closed, by reason: exhausted, killed, server_expired, error or abandoned",
            "target_collection",
            "target_db",
            "reason");
        IdleTimeout = session.Options.CursorIdleTimeout;

        _openCursorsCache = new(key => OpenCursors.WithLabels(key.Item1, key.Item2));
        _lifetimeCache = new(key => CursorLifetime.WithLabels(key.Item1, key.Item2));
        _documentsCache = new(key => CursorDocuments.WithLabels(key.Item1, key.Item2));
        _closedCache = new(key => CursorsClosed.WithLabels(key.Item1, key.Item2, key.Item3));
    }

    internal GaugeFamily OpenCursors { get; }

    internal HistogramFamily CursorLifetime { get; }

    internal HistogramFamily CursorDocuments { get; }

    internal CounterFamily CursorsClosed { get; }

    /// <summary>
    /// How long a cursor may go without a batch before it counts as abandoned
    /// (<see cref="Contrib.MongoDb.MongoInstrumentationOptions.CursorIdleTimeout"/>). The client
    /// cannot read the server's <c>cursorTimeoutMillis</c>.
    /// </summary>
    internal TimeSpan IdleTimeout { get; set; }

    internal int CursorsOpen => _cursors.Count;

    public void Handle(MongoCommandEventSuccess e)
    {
        if (e.OperationType is MongoOperationType.KillCursors)
        {
            CloseKilled(e);
            return;
        }

        if (e.BatchDocumentCount is not { } documents)
        {
            return;
        }

        if (e.IsFirstBatch)
        {
            if (e.IsFinalBatch || e.CursorId is not (long id and not 0))
            {
                // A single-batch reply never opens a cursor: no lifetime, no close reason.
                _documentsCache.Get((e.TargetCollection, e.TargetDatabase)).Observe(documents);
                return;
            }

            var state = new CursorState(e.TargetCollection, e.TargetDatabase, Stopwatch.GetTimestamp(), documents, e.CursorNeverTimesOut);
            if (_cursors.TryAdd((e.ServerEndPoint, id), state))
            {
                _openCursorsCache.Get((state.Collection, state.Database)).Inc();
            }

            return;
        }

        if (e.CursorId is not { } cursorId || !_cursors.TryGetValue((e.ServerEndPoint, cursorId), out var cursor))
        {
            return;
        }

        cursor.Fetched(documents, Stopwatch.GetTimestamp());
        if (e.IsFinalBatch)
        {
            Close((e.ServerEndPoint, cursorId), Exhausted, Stopwatch.GetTimestamp());
        }
    }

    public void Handle(MongoCommandEventFailure e)
    {
        // The application disposed these cursors; a failed kill does not reopen them.
        if (e.OperationType is MongoOperationType.KillCursors)
        {
            CloseKilled(e);
            return;
        }

        // A failed getMore ends the cursor for the application (the driver discards it).
        // Client-side errors such as timeouts may leave the server cursor alive until the
        // server reaps it, but the application no longer holds it.
        if (e.OperationType is MongoOperationType.GetMore && e.CursorId is { } cursorId)
        {
            Close((e.ServerEndPoint, cursorId), IsCursorNotFound(e.Failure) ? ServerExpired : Error, Stopwatch.GetTimestamp());
        }
    }

    /// <summary>
    /// Closes every cursor idle for longer than <see cref="IdleTimeout"/> as abandoned.
    /// Cursors the server never times out are skipped. Runs each time metrics are collected.
    /// </summary>
    public void Sweep(long nowTimestamp)
    {
        var idleTicks = (long)(IdleTimeout.TotalSeconds * Stopwatch.Frequency);
        foreach (var (key, cursor) in _cursors)
        {
            if (!cursor.NeverTimesOut && nowTimestamp - cursor.LastActivityTimestamp > idleTicks)
            {
                Close(key, Abandoned, nowTimestamp);
            }
        }
    }

    private static bool IsCursorNotFound(Exception? failure) =>
        failure is MongoCursorNotFoundException || CommandErrorsMetricProvider.ServerCode(failure) == CursorNotFoundCode;

    private void CloseKilled(MongoCommandEvent e)
    {
        if (e.KilledCursorIds is not { } killedCursorIds)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        foreach (var cursorId in killedCursorIds)
        {
            Close((e.ServerEndPoint, cursorId), Killed, now);
        }
    }

    // The only place cursor metrics are published for an open cursor; removing the entry
    // first makes the close happen exactly once even when a kill races a final batch.
    // Labels come from the command that opened the cursor, never the one closing it.
    private void Close((EndPoint? Server, long CursorId) key, string reason, long nowTimestamp)
    {
        if (!_cursors.TryRemove(key, out var cursor))
        {
            return;
        }

        var labels = (cursor.Collection, cursor.Database);
        _openCursorsCache.Get(labels).Dec();
        _lifetimeCache.Get(labels).Observe((nowTimestamp - cursor.OpenedTimestamp) / (double)Stopwatch.Frequency);
        _documentsCache.Get(labels).Observe(cursor.Documents);
        _closedCache.Get((cursor.Collection, cursor.Database, reason)).Inc();
    }

    private sealed class CursorState(string collection, string database, long openedTimestamp, int documents, bool neverTimesOut)
    {
        private int _documents = documents;
        private long _lastActivityTimestamp = openedTimestamp;

        public string Collection { get; } = collection;

        public string Database { get; } = database;

        public long OpenedTimestamp { get; } = openedTimestamp;

        public bool NeverTimesOut { get; } = neverTimesOut;

        public int Documents => Volatile.Read(ref _documents);

        public long LastActivityTimestamp => Volatile.Read(ref _lastActivityTimestamp);

        public void Fetched(int documents, long nowTimestamp)
        {
            Interlocked.Add(ref _documents, documents);
            Volatile.Write(ref _lastActivityTimestamp, nowTimestamp);
        }
    }
}
