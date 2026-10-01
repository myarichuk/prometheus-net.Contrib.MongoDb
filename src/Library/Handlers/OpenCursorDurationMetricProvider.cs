using System.Collections.Concurrent;
using System.Diagnostics;
using Prometheus;
using PrometheusNet.MongoDb;
using PrometheusNet.MongoDb.Events;
using PrometheusNet.MongoDb.Handlers;

namespace PrometheusNet.Contrib.MongoDb.Handlers
{
    internal class OpenCursorDurationMetricProvider : IMongoDbClientMetricProvider
    {
        // Operation id -> monotonic start timestamp (see Stopwatch.GetTimestamp).
        // DateTime.UtcNow is wall-clock time: coarse and non-monotonic, wrong tool for durations.
        private readonly ConcurrentDictionary<long, long> _cursorStartTimestamps = new();

        internal int CursorsOpen => _cursorStartTimestamps.Count;

        /// <summary>
        /// Histogram metric for tracking the duration a MongoDB cursor is open.
        /// </summary>
        /// <remarks>This is done in seconds</remarks>
        internal readonly Histogram OpenCursorDuration = Metrics.CreateHistogram(
            "mongodb_client_open_cursors_duration",
            "Duration a MongoDB cursor is open in seconds",
            new HistogramConfiguration
            {
                Buckets = new[] { 0.1, 1, 5, 30 },
                LabelNames = new[] { "target_collection", "target_db" },
            });


        private readonly MetricChildCache<(string, string), Histogram.Child> _openDurationCache;

        public OpenCursorDurationMetricProvider() =>
            _openDurationCache = new(key => OpenCursorDuration.WithLabels(key.Item1, key.Item2));

        public void Handle(MongoCommandEventSuccess e)
        {
            // killCursors closes a cursor that was abandoned before its final batch.
            if (e.OperationType is MongoOperationType.KillCursors)
            {
                ObserveAndRemove(e);
                return;
            }

            if (e.IsFirstBatch)
            {
                // Mark the start time for this cursor
                _cursorStartTimestamps[e.OperationId] = Stopwatch.GetTimestamp();
            }

            if (e.IsFinalBatch)
            {
                // Calculate duration and record it if this is the final batch
                ObserveAndRemove(e);
            }
        }

        private void ObserveAndRemove(MongoCommandEventSuccess e)
        {
            if (_cursorStartTimestamps.TryRemove(e.OperationId, out var startTimestamp))
            {
                var duration = (Stopwatch.GetTimestamp() - startTimestamp) / (double)Stopwatch.Frequency;

                _openDurationCache
                    .Get((e.TargetCollection, e.TargetDatabase))
                    .Observe(duration);
            }
        }

        public void Handle(MongoCommandEventFailure e)
        {
            // A failed cursor will never produce a final batch; drop the start time so the
            // entry neither leaks nor corrupts a later, unrelated cursor on the same operation id.
            _cursorStartTimestamps.TryRemove(e.OperationId, out _);
        }
    }
}
