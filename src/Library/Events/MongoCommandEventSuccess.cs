namespace PrometheusNet.MongoDb.Events;

public class MongoCommandEventSuccess : MongoCommandEvent
{
    public byte[] RawReply { get; set; }

    public Dictionary<string, object> Reply { get; set; }
}