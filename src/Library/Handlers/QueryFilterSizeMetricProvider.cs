using System.Buffers;
using MongoDB.Bson;
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
        private readonly MetricChildCache<(string, string, string), Histogram.Child> _filterSizeCache;

        public QueryFilterSizeMetricProvider() =>
            _filterSizeCache = new(key => QueryFilterSize.WithLabels(key.Item1, key.Item2, key.Item3));

        public void Handle(MongoCommandEventStart e)
        {
            if (e.OperationType is MongoOperationType.Find or MongoOperationType.Aggregate &&
                e.FilterDocument is { } filter)
            {
                // recursively count the amount of elements in the filter, regardless of binary operators
                var filterSize = CalculateFilterSize(filter);

                _filterSizeCache
                    .Get((e.OperationRawType, e.TargetCollection, e.TargetDatabase))
                    .Observe(filterSize);
            }
        }

        internal static int CalculateFilterSize(BsonDocument filter)
        {
            // Iterative on purpose: filters can nest thousands deep (large $and/$or/$in
            // trees) and recursion would overflow the stack. BsonValues are reference
            // types, so no boxing is involved while walking.
            //
            // The explicit stack is rented from the shared ArrayPool instead of using
            // Stack<T>: a per-query Stack object plus its geometric growth arrays is
            // pure Gen0 garbage, while a rented buffer costs nothing to reuse. Popped
            // slots are intentionally not cleared (the whole buffer is returned with
            // clearArray: true), which is safe because nothing reads below the top.
            var rented = ArrayPool<BsonValue>.Shared.Rent(64);
            var top = 0;
            rented[top++] = filter;

            var totalSize = 0;

            try
            {
                while (top > 0)
                {
                    var filterElement = rented[--top];

                    switch (filterElement)
                    {
                        case BsonDocument nestedFilter:
                            foreach (var element in nestedFilter)
                            {
                                if (top == rented.Length)
                                {
                                    rented = Grow(rented, top);
                                }

                                rented[top++] = element.Value;
                            }

                            break;

                        case BsonArray array:
                            foreach (var value in array)
                            {
                                if (top == rented.Length)
                                {
                                    rented = Grow(rented, top);
                                }

                                rented[top++] = value;
                            }

                            break;

                        default:
                            totalSize++;
                            break;
                    }
                }
            }
            finally
            {
                ArrayPool<BsonValue>.Shared.Return(rented, clearArray: true);
            }

            return totalSize;
        }

        private static BsonValue[] Grow(BsonValue[] rented, int count)
        {
            var bigger = ArrayPool<BsonValue>.Shared.Rent(rented.Length * 2);
            Array.Copy(rented, bigger, count);
            ArrayPool<BsonValue>.Shared.Return(rented, clearArray: true);
            return bigger;
        }
    }
}
