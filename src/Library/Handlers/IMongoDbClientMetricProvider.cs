using MongoDB.Driver.Core.Events;
using PrometheusNet.MongoDb.Events;

namespace PrometheusNet.MongoDb.Handlers;

/// <summary>
/// A metric provider. Override only the handlers you need: each one is subscribed only when
/// it is overridden. Command events are this library's own, with the command parsed once for every
/// provider; connection, pool, server selection and server monitoring events are the
/// driver's own structs, passed through as they are. Handlers run on the driver's thread, so keep them fast.
/// </summary>
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

    void Handle(ConnectionOpenedEvent e)
    {
    }

    void Handle(ConnectionClosedEvent e)
    {
    }

    void Handle(ConnectionFailedEvent e)
    {
    }

    void Handle(ConnectionPoolCheckedOutConnectionEvent e)
    {
    }

    void Handle(ConnectionPoolCheckingOutConnectionFailedEvent e)
    {
    }

    void Handle(ConnectionPoolCheckedInConnectionEvent e)
    {
    }

    void Handle(ConnectionPoolAddedConnectionEvent e)
    {
    }

    void Handle(ConnectionPoolRemovedConnectionEvent e)
    {
    }

    void Handle(ConnectionPoolClearedEvent e)
    {
    }

    void Handle(ClusterSelectedServerEvent e)
    {
    }

    void Handle(ClusterSelectingServerFailedEvent e)
    {
    }

    void Handle(ServerHeartbeatFailedEvent e)
    {
    }

    void Handle(ServerDescriptionChangedEvent e)
    {
    }
}
