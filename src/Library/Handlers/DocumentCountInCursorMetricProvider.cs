using System.Collections.Concurrent;
using Prometheus;
using PrometheusNet.MongoDb;
using PrometheusNet.MongoDb.Events;
using PrometheusNet.MongoDb.Handlers;

namespace PrometheusNet.Contrib.MongoDb.Handlers
{
    internal class DocumentCountInCursorMetricProvider : IMongoDbClientMetricProvider
    {
        private readonly ConcurrentDictionary<long, int> _documentCountsPerOperationId = new();

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
            if (_documentCountsPerOperationId.TryRemove(e.OperationId, out var documentCount))
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
            if (e.BatchDocumentCount is { } documentCount)
            {
                _documentCountsPerOperationId.AddOrUpdate(
                    e.OperationId,
                    documentCount,
                    (_, existing) => existing + documentCount);
            }

            if (e.IsFinalBatch && _documentCountsPerOperationId.TryRemove(e.OperationId, out documentCount))
            {
                _documentCountCache
                    .Get((e.TargetCollection, e.TargetDatabase))
                    .Observe(documentCount);
            }
        }
    }
}
