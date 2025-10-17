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

        private int CalculateFilterSize(Dictionary<string, object> filter)
        {
            var filterElements = new Stack<object>();
            filterElements.Push(filter);

            var totalSize = 0;

            while (filterElements.Count > 0)
            {
                var filterElement = filterElements.Pop();

                switch (filterElement)
                {
                    case Dictionary<string, object> nestedFilter:
                        foreach (var value in nestedFilter.Values)
                        {
                            filterElements.Push(value);
                        }

                        break;

                    case IEnumerable<object> enumerable:
                        foreach (var value in enumerable)
                        {
                            filterElements.Push(value);
                        }

                        break;

                    default:
                        totalSize++;
                        break;
                }
            }

            return totalSize;
        }

    }
}
