using System.Collections.Concurrent;
using System.Diagnostics;
using PrometheusNet.MongoDb;
using PrometheusNet.MongoDb.Events;
using PrometheusNet.MongoDb.Handlers;

namespace PrometheusNet.Contrib.MongoDb.Handlers;

/// <summary>
/// Counts commands the driver retried (retryable reads and writes). A retry reuses the
/// operation id of the attempt that failed, so a failure followed by a start of the same
/// command under the same operation id is a retry. Failures wait for their retry for
/// <see cref="RetryWindow"/>; the ones never retried are dropped each time metrics are collected.
/// </summary>
internal sealed class CommandRetryMetricProvider : IMongoDbClientMetricProvider, ISweepingProvider
{
    // Operation id -> the failed command and when it failed. Only failures are written, so
    // the map is empty while nothing fails, and a start pays one lock-free lookup.
    private readonly ConcurrentDictionary<long, (string Command, long FailedTimestamp)> _failed = new();

    private readonly MetricChildCache<(string, string, string), CounterChild> _retriesCache;

    public CommandRetryMetricProvider(InstrumentationSession session)
    {
        Retries = session.Sink.Counter(
            "mongodb_client_command_retries_total",
            "Commands the driver retried after a failed attempt",
            "command_type",
            "target_collection",
            "target_db");
        _retriesCache = new(key => Retries.WithLabels(key.Item1, key.Item2, key.Item3));
    }

    internal CounterFamily Retries { get; }

    /// <summary>
    /// How long a failure waits for its retry. The driver retries once, right after
    /// selecting a server again, so this only has to outlast server selection (30 s by default).
    /// </summary>
    internal TimeSpan RetryWindow { get; set; } = TimeSpan.FromMinutes(2);

    internal bool IsPending(long operationId) => _failed.ContainsKey(operationId);

    /// <summary>Back-dates a pending failure, so tests can expire it without waiting.</summary>
    internal bool AgeForTests(long operationId, TimeSpan age) =>
        _failed.TryGetValue(operationId, out var failed)
        && _failed.TryUpdate(operationId, failed with { FailedTimestamp = failed.FailedTimestamp - (long)(age.TotalSeconds * Stopwatch.Frequency) }, failed);

    public void Handle(MongoCommandEventFailure e)
    {
        if (e.OperationId != 0)
        {
            _failed[e.OperationId] = (e.OperationRawType, Stopwatch.GetTimestamp());
        }
    }

    public void Handle(MongoCommandEventStart e)
    {
        if (e.OperationId == 0
            || !_failed.TryGetValue(e.OperationId, out var failed)
            || !string.Equals(failed.Command, e.OperationRawType, StringComparison.Ordinal)
            || !_failed.TryRemove(e.OperationId, out _))
        {
            return;
        }

        _retriesCache.Get((e.OperationRawType, e.TargetCollection, e.TargetDatabase)).Inc();
    }

    public void Sweep(long nowTimestamp)
    {
        var windowTicks = (long)(RetryWindow.TotalSeconds * Stopwatch.Frequency);
        foreach (var (operationId, failed) in _failed)
        {
            if (nowTimestamp - failed.FailedTimestamp > windowTicks)
            {
                _failed.TryRemove(operationId, out _);
            }
        }
    }
}
