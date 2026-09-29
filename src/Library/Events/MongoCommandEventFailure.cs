namespace PrometheusNet.MongoDb.Events;

public class MongoCommandEventFailure : MongoCommandEvent
{
    public Exception Failure { get; set; } = default!;

    internal override void Reset()
    {
        base.Reset();
        Failure = null!;
    }
}
