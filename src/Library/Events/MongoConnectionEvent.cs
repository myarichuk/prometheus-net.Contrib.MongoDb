// Ignore Spelling: Mongo Contrib

namespace PrometheusNet.Contrib.MongoDb.Events
{
    public record MongoConnectionEvent
    {
        public string Endpoint { get; set; } = string.Empty;

        public int ClusterId { get; set; }
    }
}
