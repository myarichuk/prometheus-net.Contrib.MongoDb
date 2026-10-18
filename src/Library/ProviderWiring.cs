using MongoDB.Driver.Core.Events;
using PrometheusNet.MongoDb.Events;
using PrometheusNet.MongoDb.Handlers;

namespace PrometheusNet.MongoDb;

/// <summary>
/// Subscribes a provider to the events it handles. Providers implement handlers via default
/// interface methods, so a provider that only cares about (say) command successes must not
/// pay for no-op subscriptions that would run on every unrelated event: only the handlers a
/// provider overrides are subscribed.
/// </summary>
internal static class ProviderWiring
{
    private static readonly EventWiring[] EventWirings =
    [
        Wire<MongoCommandEventStart>(static p => p.Handle),
        Wire<MongoCommandEventFailure>(static p => p.Handle),
        Wire<MongoCommandEventSuccess>(static p => p.Handle),
        Wire<ConnectionOpenedEvent>(static p => p.Handle),
        Wire<ConnectionClosedEvent>(static p => p.Handle),
        Wire<ConnectionFailedEvent>(static p => p.Handle),
        Wire<ConnectionPoolCheckedOutConnectionEvent>(static p => p.Handle),
        Wire<ConnectionPoolCheckingOutConnectionFailedEvent>(static p => p.Handle),
        Wire<ConnectionPoolCheckedInConnectionEvent>(static p => p.Handle),
        Wire<ConnectionPoolAddedConnectionEvent>(static p => p.Handle),
        Wire<ConnectionPoolRemovedConnectionEvent>(static p => p.Handle),
        Wire<ConnectionPoolClearedEvent>(static p => p.Handle),
        Wire<ClusterSelectedServerEvent>(static p => p.Handle),
        Wire<ClusterSelectingServerFailedEvent>(static p => p.Handle),
        Wire<ServerHeartbeatFailedEvent>(static p => p.Handle),
        Wire<ServerDescriptionChangedEvent>(static p => p.Handle),
    ];

    private readonly record struct EventWiring(Type EventType, Action<EventHub, IMongoDbClientMetricProvider> Subscribe);

    public static void Subscribe(EventHub hub, IMongoDbClientMetricProvider provider)
    {
        foreach (var wiring in EventWiringsFor(provider.GetType()))
        {
            wiring.Subscribe(hub, provider);
        }
    }

    /// <summary>The event types a provider type overrides a handler for.</summary>
    internal static IEnumerable<Type> HandledEventTypes(Type providerType) =>
        EventWiringsFor(providerType).Select(static wiring => wiring.EventType);

    private static EventWiring Wire<TEvent>(Func<IMongoDbClientMetricProvider, Action<TEvent>> handler) =>
        new(typeof(TEvent), (hub, provider) => hub.Subscribe(handler(provider)));

    private static IEnumerable<EventWiring> EventWiringsFor(Type providerType)
    {
        var interfaceMap = providerType.GetInterfaceMap(typeof(IMongoDbClientMetricProvider));

        for (var i = 0; i < interfaceMap.InterfaceMethods.Length; i++)
        {
            // Default interface implementations live on the interface itself; anything else
            // is a genuine override that deserves a subscription.
            if (interfaceMap.TargetMethods[i].DeclaringType == typeof(IMongoDbClientMetricProvider))
            {
                continue;
            }

            var eventType = interfaceMap.InterfaceMethods[i].GetParameters()[0].ParameterType;
            foreach (var wiring in EventWirings)
            {
                if (wiring.EventType == eventType)
                {
                    yield return wiring;
                    break;
                }
            }
        }
    }
}
