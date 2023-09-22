namespace PrometheusNet.MongoDb.Events;

public class MongoCommandEventFailure : MongoCommandEvent
{
    public Exception Failure { get; set; }
}