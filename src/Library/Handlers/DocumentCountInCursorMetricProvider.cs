using System.Collections.Concurrent;
using Prometheus;
using PrometheusNet.MongoDb;
using PrometheusNet.MongoDb.Events;
using PrometheusNet.MongoDb.Handlers;

namespace PrometheusNet.Contrib.MongoDb.Handlers
{
    internal class DocumentCountInCursorMetricProvider : IMongoDbClientMetricProvider
    {
        // Accumulated batch document counts per cursor key (see MongoCommandEvent.CursorKey).
        // Keying by cursor id (not operation id) keeps find/getMore batches of one cursor
        // together and still matches a killCursors, which runs as its own operation.
        private readonly ConcurrentDictionary<long, int> _documentCountsPerCursor = new();

        /// <summary>
        /// Summary metric for tracking the number of documents fetched per cursor batch.
        /// </summary>
        internal readonly Summary DocumentCountInCursor = Metrics.CreateSummary(
            "mongodb_client_cursor_document_count",
            "Number of documents fetched per cursor batch (note the operationId label)",
            new SummaryConfiguration
            {
                LabelNames = new[] { "target_collection", "target_db" }
            });

        private readonly MetricChildCache<(string, string), Summary.Child> _documentCountCache;

        public DocumentCountInCursorMetricProvider() =>
            _documentCountCache = new(key => DocumentCountInCursor.WithLabels(key.Item1, key.Item2));

        public void Handle(MongoCommandEventFailure e)
        {
            if (_documentCountsPerCursor.TryRemove(e.CursorKey, out var documentCount))
            {
                _documentCountCache
                    .Get((e.TargetCollection, e.TargetDatabase))
                    .Observe(documentCount);
            }
        }

        /// <summary>
        /// Handles the MongoDB command event to extract document counts from the cursor.
        /// Only cursor-bearing replies accumulate state; everything else is ignored, so
        /// non-cursor commands (inserts, updates, ...) never leave entries behind.
        /// </summary>
        /// <param name="e">The MongoDB command event.</param>
        public void Handle(MongoCommandEventSuccess e)
        {
            // killCursors closes cursors abandoned before their final batch: publish
            // whatever was accumulated for each killed cursor. It runs as its own driver
            // operation, so the cursors are matched by id, never by operation id.
            if (e.OperationType is MongoOperationType.KillCursors)
            {
                if (e.KilledCursorIds is { } killedCursorIds)
                {
                    foreach (var cursorId in killedCursorIds)
                    {
                        if (_documentCountsPerCursor.TryRemove(cursorId, out var killedCount))
                        {
                            _documentCountCache
                                .Get((e.TargetCollection, e.TargetDatabase))
                                .Observe(killedCount);
                        }
                    }
                }

                return;
            }

            if (e.IsFinalBatch)
            {
                // Closing batch (id 0, or a cursor that never stayed open): publish this
                // batch plus anything accumulated under the same key. Combining here
                // instead of accumulate-then-remove keeps single-batch cursors exact even
                // when many close concurrently.
                var total = e.BatchDocumentCount ?? 0;
                if (_documentCountsPerCursor.TryRemove(e.CursorKey, out var accumulated))
                {
                    total += accumulated;
                }
                else if (e.BatchDocumentCount is null)
                {
                    return;
                }

                _documentCountCache
                    .Get((e.TargetCollection, e.TargetDatabase))
                    .Observe(total);
            }
            else if (e.BatchDocumentCount is { } documentCount)
            {
                _documentCountsPerCursor.AddOrUpdate(
                    e.CursorKey,
                    documentCount,
                    (_, existing) => existing + documentCount);
            }
        }
    }
}
