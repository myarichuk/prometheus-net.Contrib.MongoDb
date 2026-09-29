using System.Collections.Concurrent;
using System.Reflection;
using PrometheusNet.Contrib.MongoDb.Events;
using PrometheusNet.MongoDb.Events;
using PrometheusNet.MongoDb.Handlers;

// ReSharper disable CollectionNeverQueried.Local

namespace PrometheusNet.MongoDb;

internal static class MetricProviderRegistrar
{
    private static bool _isRegistered;

    // just in case, for testing mostly
    static MetricProviderRegistrar() => RegisterAll();

    // needed to prevent GC from collecting metric providers
    private static readonly ConcurrentDictionary<Type, IMongoDbClientMetricProvider> MetricsProviders = new();

    // Every event type a provider can handle, mapped to subscribe/unsubscribe actions.
    // Providers implement handlers via default interface methods, so a provider that only
    // cares about (say) command successes must not pay for five no-op subscriptions that
    // would run on every unrelated event.
    private static readonly (Type EventType, Action<EventHub, IMongoDbClientMetricProvider> Subscribe, Action<EventHub, IMongoDbClientMetricProvider> Unsubscribe)[] EventWirings =
    [
        (
            typeof(MongoCommandEventStart),
            static (hub, provider) => hub.Subscribe<MongoCommandEventStart>(provider.Handle),
            static (hub, provider) => hub.Unsubscribe<MongoCommandEventStart>(provider.Handle)),
        (
            typeof(MongoCommandEventFailure),
            static (hub, provider) => hub.Subscribe<MongoCommandEventFailure>(provider.Handle),
            static (hub, provider) => hub.Unsubscribe<MongoCommandEventFailure>(provider.Handle)),
        (
            typeof(MongoCommandEventSuccess),
            static (hub, provider) => hub.Subscribe<MongoCommandEventSuccess>(provider.Handle),
            static (hub, provider) => hub.Unsubscribe<MongoCommandEventSuccess>(provider.Handle)),
        (
            typeof(MongoConnectionOpenedEvent),
            static (hub, provider) => hub.Subscribe<MongoConnectionOpenedEvent>(provider.Handle),
            static (hub, provider) => hub.Unsubscribe<MongoConnectionOpenedEvent>(provider.Handle)),
        (
            typeof(MongoConnectionClosedEvent),
            static (hub, provider) => hub.Subscribe<MongoConnectionClosedEvent>(provider.Handle),
            static (hub, provider) => hub.Unsubscribe<MongoConnectionClosedEvent>(provider.Handle)),
        (
            typeof(MongoConnectionFailedEvent),
            static (hub, provider) => hub.Subscribe<MongoConnectionFailedEvent>(provider.Handle),
            static (hub, provider) => hub.Unsubscribe<MongoConnectionFailedEvent>(provider.Handle)),
    ];

    public static void ReplaceForTests<TProvider>(TProvider newProvider)
        where TProvider : class, IMongoDbClientMetricProvider
    {
        if (MetricsProviders.TryRemove(typeof(TProvider), out var removedProvider))
        {
            UnsubscribeProvider(removedProvider);
        }

        if (MetricsProviders.TryAdd(typeof(TProvider), newProvider))
        {
            SubscribeProvider(newProvider);
        }
    }

    public static void RegisterAll()
    {
        RegisterAll(new[] { typeof(MetricProviderRegistrar).Assembly });
    }

    public static void RegisterAll(IEnumerable<Assembly> assemblies)
    {
        var assembliesToScan = assemblies?.ToArray() ?? Array.Empty<Assembly>();
        if (assembliesToScan.Length == 0)
        {
            assembliesToScan = new[] { typeof(MetricProviderRegistrar).Assembly };
        }

        lock (MetricsProviders)
        {
            if (_isRegistered)
            {
                return;
            }

            _isRegistered = true;
        }

        foreach (var metricProviderType in EnumerateIMetricsHandlers(assembliesToScan))
        {
            var metricProvider = (IMongoDbClientMetricProvider)Activator.CreateInstance(metricProviderType)!;
            MetricsProviders.TryAdd(metricProviderType, metricProvider);

            SubscribeProvider(metricProvider);
        }
    }

    public static bool TryGetProvider<TProvider>(out TProvider? provider) where TProvider : class, IMongoDbClientMetricProvider
    {
        var success = MetricsProviders.TryGetValue(typeof(TProvider), out var providerAsObject);
        return success ?
            (provider = (TProvider)providerAsObject!) != null :
            (provider = null) != null;
    }

    private static void SubscribeProvider(IMongoDbClientMetricProvider provider)
    {
        foreach (var (eventType, subscribe, _) in EventWiringsFor(provider.GetType()))
        {
            subscribe(EventHub.Default, provider);
        }
    }

    private static void UnsubscribeProvider(IMongoDbClientMetricProvider provider)
    {
        foreach (var (eventType, _, unsubscribe) in EventWiringsFor(provider.GetType()))
        {
            unsubscribe(EventHub.Default, provider);
        }
    }

    private static IEnumerable<(Type EventType, Action<EventHub, IMongoDbClientMetricProvider> Subscribe, Action<EventHub, IMongoDbClientMetricProvider> Unsubscribe)> EventWiringsFor(Type providerType)
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

    private static IEnumerable<Type> EnumerateIMetricsHandlers(IEnumerable<Assembly> assemblies)
    {
        foreach (var assembly in assemblies)
        {
            if (assembly is null || assembly.IsDynamic)
            {
                continue;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t is not null).ToArray()!;
            }

            foreach (var type in types)
            {
                if (type is not null && typeof(IMongoDbClientMetricProvider).IsAssignableFrom(type) && !type.IsInterface && !type.IsAbstract)
                {
                    yield return type;
                }
            }
        }
    }
}
