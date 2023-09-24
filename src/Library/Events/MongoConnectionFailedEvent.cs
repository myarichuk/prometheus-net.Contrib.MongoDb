namespace PrometheusNet.Contrib.MongoDb.Events
{

    internal record MongoConnectionFailedEvent : MongoConnectionEvent
    {
        public Exception Exception { get; set; }
    }
}
