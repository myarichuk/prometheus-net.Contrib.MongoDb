using Prometheus;
using PrometheusNet.MongoDb.Events;

// ReSharper disable UnusedMember.Global
#pragma warning disable SA1413

namespace PrometheusNet.MongoDb.Handlers
{
    public class CommandResponseSizeProvider: IMetricProvider
    {
        /// <summary>
        /// Histogram metric to measure the size of MongoDB command responses.
        /// Buckets are configured to capture various ranges of response sizes.
        /// </summary>
        public static readonly Histogram CommandResponseSize = Metrics.CreateHistogram(
            "mongodb_command_response_size", // Metric name
            "Size of the MongoDB command responses (in bytes)", // Help text
            new HistogramConfiguration
            {
                LabelNames = new[] { "command_type", "target_collection", "target_db" },
                Buckets = new[] { 100.0, 500.0, 1000.0, 2000.0, 5000.0, 10000.0, 20000.0, 50000.0, 100000.0, double.PositiveInfinity }
            });

        public void Handle(MongoCommandEventStart e)
        {
        }

        public void Handle(MongoCommandEventSuccess e)
        {
            try
            {
                var replySize = e.RawReply.Length;

                CommandResponseSize
                    .WithLabels(e.OperationRawType, e.TargetCollection, e.TargetDatabase)
                    .Observe(replySize);
            }
            catch (OverflowException ex)
            {
            }
        }

        public void Handle(MongoCommandEventFailure e)
        {
        }
    }
}
