using PrometheusNet.Contrib.MongoDb.Events;
using PrometheusNet.MongoDb.Events;

namespace PrometheusNet.MongoDb.Handlers;

public interface IMongoDbClientMetricProvider
{
    void Handle(MongoCommandEventStart e)
    {
    }

    void Handle(MongoCommandEventSuccess e)
    {
    }

    void Handle(MongoCommandEventFailure e)
    {
    }

    void Handle(MongoConnectionOpenedEvent e)
    {
    }

    void Handle(MongoConnectionClosedEvent e)
    {
    }

    void Handle(MongoConnectionFailedEvent e)
    {
    }
}