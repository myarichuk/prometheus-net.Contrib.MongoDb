namespace PrometheusNet.Contrib.MongoDb.Events
{
    public record MongoConnectionFailedEvent : MongoConnectionEvent
    {
        public Exception Exception { get; set; }
    }
}
