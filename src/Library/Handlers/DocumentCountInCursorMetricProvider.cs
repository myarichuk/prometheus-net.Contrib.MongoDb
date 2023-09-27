using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Prometheus;
using PrometheusNet.MongoDb.Events;
using PrometheusNet.MongoDb.Handlers;

namespace PrometheusNet.Contrib.MongoDb.Handlers
{
    internal class DocumentCountInCursorMetricProvider : IMetricProvider
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

        public void Handle(MongoCommandEventStart e)
        {
            throw new NotImplementedException();
        }

        public void Handle(MongoCommandEventFailure e)
        {
            if (_documentCountsPerOperationId.TryRemove(e.OperationId, out var documentCount))
            {
                DocumentCountInCursor
                    .WithLabels(e.TargetCollection, e.TargetDatabase)
                    .Observe(documentCount);
            }
        }

        /// <summary>
        /// Handles the MongoDB command event to extract document counts from the cursor.
        /// </summary>
        /// <param name="e">The MongoDB command event.</param>
        public void Handle(MongoCommandEventSuccess e)
        {
            if (TryGetDocumentCountFromReply(e.Reply, out var documentCount))
            {
                _documentCountsPerOperationId.AddOrUpdate(
                    e.OperationId,
                    documentCount,
                    (_, existing) => existing + documentCount);
            }

            if (IsFinalBatch(e.Reply) && _documentCountsPerOperationId.TryRemove(e.OperationId, out documentCount))
            {
                DocumentCountInCursor
                    .WithLabels(e.TargetCollection, e.TargetDatabase)
                    .Observe(documentCount);
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

        /// <summary>
        /// Tries to get the document count from the command reply.
        /// </summary>
        /// <param name="commandReply">The command reply from MongoDB.</param>
        /// <param name="documentCount">The output parameter for the document count.</param>
        /// <returns>True if the document count is successfully obtained, false otherwise.</returns>
        private static bool TryGetDocumentCountFromReply(Dictionary<string, object> commandReply, out int documentCount)
        {
            documentCount = 0;

            if (commandReply.TryGetValue("cursor", out var cursorAsObject) &&
                cursorAsObject is Dictionary<string, object> cursor)
            {
                if (cursor.TryGetValue("firstBatch", out var batchDocumentAsObject) &&
                    batchDocumentAsObject is object[] firstBatchDocuments)
                {
                    documentCount = firstBatchDocuments.Length;
                    return true;
                }

                if (cursor.TryGetValue("nextBatch", out var nextBatchDocumentAsObject) &&
                    nextBatchDocumentAsObject is object[] nextBatchDocuments)
                {
                    documentCount = nextBatchDocuments.Length;
                    return true;
                }
            }

            return false;
        }
    }
}
