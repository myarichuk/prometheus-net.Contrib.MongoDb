using Prometheus;
using PrometheusNet.MongoDb.Events;
using PrometheusNet.MongoDb.Handlers;

namespace PrometheusNet.Contrib.MongoDb.Handlers
{
    internal class CommandRequestSizeMetricProvider: IMongoDbClientMetricProvider
    {
        /// <summary>
        /// A histogram metric that tracks the size (in bytes) of MongoDB commands being sent.
        /// </summary>
        /// <remarks>
        /// The metric has the following labels:
        /// - command_type: The type of MongoDB operation (e.g., find, update, etc.)
        /// - target_collection: The MongoDB collection targeted by the operation
        /// - target_db: The MongoDB database targeted by the operation
        /// 
        /// The bucket sizes are in bytes and are chosen to cover a range of typical MongoDB command sizes.
        /// </remarks>
        public static readonly Histogram CommandRequestSize = Metrics.CreateHistogram(
            "mongodb_client_command_request_size",
            "Size of MongoDB commands (in bytes)",
            new HistogramConfiguration
            {
                LabelNames = new[] { "command_type", "target_collection", "target_db" },
                Buckets = new[] { 50.0, 5000.0, 25000.0, 102400.0, 1024.0 * 1024.0 }
            });

        /// <summary>
        /// Handles event when a MongoDB command starts.
        /// </summary>
        /// <param name="event">The event data associated with the MongoDB command start.</param>
        /// <remarks>
        /// This method captures the raw size (in bytes) of the MongoDB command and observes it through the histogram.
        /// </remarks>
        public void Handle(MongoCommandEventStart @event)
        {
            CommandRequestSize
                .WithLabels(@event.OperationRawType, @event.TargetCollection, @event.TargetDatabase)
                .Observe(@event.RawRequestSizeInBytes);
        }
    }
}
