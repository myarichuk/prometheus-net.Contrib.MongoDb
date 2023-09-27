using System;
using System.Collections.Generic;
using Prometheus;
using PrometheusNet.MongoDb.Events;
using PrometheusNet.MongoDb.Handlers;

namespace PrometheusNet.Contrib.MongoDb.Handlers
{
    /// <summary>
    /// Provides metrics for the count of documents in MongoDB cursor batches.
    /// </summary>
    internal class DocumentCountInCursorMetricProvider : IMetricProvider
    {
        /// <summary>
        /// Summary metric for tracking the number of documents fetched per cursor batch.
        /// </summary>
        internal readonly Summary DocumentCountInCursor = Metrics.CreateSummary(
            "mongodb_client_cursor_document_count",
            "Number of documents fetched per cursor batch (note the operationId label)",
            new SummaryConfiguration
            {
                LabelNames = new[] { "operationId", "target_collection", "target_db" }
            });

        /// <summary>
        /// Handles the MongoDB command event to extract document counts from the cursor.
        /// </summary>
        /// <param name="e">The MongoDB command event.</param>
        public void Handle(MongoCommandEventSuccess e)
        {
            if (TryGetDocumentCountFromReply(e.Reply, out var documentCount))
            {
                DocumentCountInCursor
                    .WithLabels(e.OperationId.ToString(), e.TargetCollection, e.TargetDatabase)
                    .Observe(documentCount);
            }
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
