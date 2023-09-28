using System.Collections.Concurrent;
using Prometheus;
using PrometheusNet.MongoDb.Events;
using PrometheusNet.MongoDb.Handlers;

namespace PrometheusNet.Contrib.MongoDb.Handlers
{
    internal class OpenCursorDurationMetricProvider: IMongoDbClientMetricProvider
    {
        private readonly ConcurrentDictionary<long, DateTime> _cursorStartTimes = new();

        internal int CursorsOpen => _cursorStartTimes.Count;

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


        public void Handle(MongoCommandEventSuccess e)
        {
            if (IsFirstBatch(e.Reply))
            {
                // Mark the start time for this cursor
                _cursorStartTimes[e.OperationId] = DateTime.UtcNow;
            }
            
            if (IsFinalBatch(e.Reply))
            {
                // Calculate duration and record it if this is the final batch
                if (_cursorStartTimes.TryRemove(e.OperationId, out var startTime))
                {
                    var duration = (DateTime.UtcNow - startTime).TotalSeconds;

                    OpenCursorDuration
                        .WithLabels(e.TargetCollection, e.TargetDatabase)
                        .Observe(duration);

                }
            }
        }

        private static bool IsFinalBatch(Dictionary<string, object> commandReply)
        {
            if (commandReply.TryGetValue("cursor", out var cursorAsObject) &&
                cursorAsObject is Dictionary<string, object> cursor)
            {
                if (cursor.TryGetValue("id", out var cursorIdAsObject) &&
                    cursorIdAsObject is long cursorId)
                {
                    return cursorId == 0;
                }
            }

            return false;
        }

        private static bool IsFirstBatch(Dictionary<string, object> commandReply)
        {
            if (commandReply.TryGetValue("cursor", out var cursorAsObject) &&
                cursorAsObject is Dictionary<string, object> cursor)
            {
                return cursor.ContainsKey("firstBatch");
            }

            return false;
        }
    }
}
