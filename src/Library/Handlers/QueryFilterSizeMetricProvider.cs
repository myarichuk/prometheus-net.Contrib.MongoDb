using Prometheus;
using PrometheusNet.MongoDb.Events;

namespace PrometheusNet.MongoDb.Handlers
{
    /// <summary>
    /// Provides functionality for tracking and recording the size of MongoDB query filters.
    /// </summary>
    internal class QueryFilterSizeMetricProvider : IMongoDbClientMetricProvider
    {
        /// <summary>
        /// A histogram metric that captures the size of MongoDB query filters.
        /// </summary>
        public readonly Histogram QueryFilterSize = Metrics.CreateHistogram(
            "mongodb_client_query_filter_size",
            "Size of MongoDB query filters",
            new HistogramConfiguration
            {
                LabelNames = new[] { "query_type", "target_collection", "target_db" },
                Buckets = new[] { 5.0, 10.0, 50.0, 250.0 },
            });

        /// <summary>
        /// Handles the event triggered when a MongoDB query is executed.
        /// </summary>
        /// <param name="e">Event information for the executed MongoDB query.</param>
        public void Handle(MongoCommandEventStart e)
        {
            if (e.OperationType is MongoOperationType.Find or MongoOperationType.Aggregate && 
                e.Command.TryGetValue("filter", out var filterAsObject) && filterAsObject is Dictionary<string, object> filter)
            {
                // recursively count the amount of elements in the filter, regardless of binary operators
                var filterSize = CalculateFilterSize(filter);

                QueryFilterSize
                    .WithLabels(e.OperationRawType, e.TargetCollection, e.TargetDatabase)
                    .Observe(filterSize);
            }
        }

        private int CalculateFilterSize(Dictionary<string, object> filter) => CalculateFilterSize(filter, 0);

        // ReSharper disable once MethodTooLong
        private int CalculateFilterSize(object filterAsObject, int currentSum)
        {
            var intermediateSum = currentSum;

            switch (filterAsObject)
            {
                case Dictionary<string, object> filter:
                    {
                        foreach (var kvp in filter)
                        {
                            intermediateSum = CalculateFilterSize(kvp.Value, intermediateSum);
                        }

                        break;
                    }

                case IEnumerable<object> filterAsArray:
                    {
                        foreach (var item in filterAsArray)
                        {
                            intermediateSum = CalculateFilterSize(item, intermediateSum);
                        }

                        break;
                    }

                default:
                    intermediateSum++;
                    break;
            }

            return intermediateSum;
        }

    }
}
