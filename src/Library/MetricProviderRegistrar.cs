using System.Collections.Concurrent;
using System.Reflection;
using PrometheusNet.Contrib.MongoDb.Events;
using PrometheusNet.MongoDb.Events;
using PrometheusNet.MongoDb.Handlers;

// ReSharper disable CollectionNeverQueried.Local

namespace PrometheusNet.MongoDb;

/// <summary>
/// Registration point for <see cref="IMongoDbClientMetricProvider"/> implementations.
/// Built-in providers are registered automatically on first use. Custom providers
/// (including ones from other assemblies) can be plugged in with
/// <see cref="RegisterAll(System.Collections.Generic.IEnumerable{System.Reflection.Assembly})"/>
/// or <see cref="Register(IMongoDbClientMetricProvider)"/>.
/// </summary>
public static class MetricProviderRegistrar
{
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
        ArgumentNullException.ThrowIfNull(newProvider);

        lock (MetricsProviders)
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
    }

    public static void RegisterAll()
    {
        RegisterAll(new[] { typeof(MetricProviderRegistrar).Assembly });
    }

    /// <summary>
    /// Registers every <see cref="IMongoDbClientMetricProvider"/> implementation found in the
    /// given assemblies. Types that are already registered are skipped, so this can be called
    /// repeatedly (e.g. once for the built-in assembly at startup, later for a custom one).
    /// </summary>
    /// <param name="assemblies">Assemblies to scan for provider implementations.</param>
    public static void RegisterAll(IEnumerable<Assembly> assemblies)
    {
        var assembliesToScan = assemblies?.ToArray() ?? Array.Empty<Assembly>();
        if (assembliesToScan.Length == 0)
        {
            assembliesToScan = new[] { typeof(MetricProviderRegistrar).Assembly };
        }

        lock (MetricsProviders)
        {
            foreach (var metricProviderType in EnumerateIMetricsHandlers(assembliesToScan))
            {
                if (MetricsProviders.ContainsKey(metricProviderType))
                {
                    continue;
                }

                var metricProvider = (IMongoDbClientMetricProvider)Activator.CreateInstance(metricProviderType)!;
                MetricsProviders[metricProviderType] = metricProvider;

                SubscribeProvider(metricProvider);
            }
        }
    }

    /// <summary>
    /// Registers a single provider instance, replacing any previously registered provider
    /// of the same type. Useful for custom providers and for overriding built-ins in tests.
    /// </summary>
    /// <param name="provider">The provider instance to register.</param>
    public static void Register(IMongoDbClientMetricProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        lock (MetricsProviders)
        {
            if (MetricsProviders.TryRemove(provider.GetType(), out var removedProvider))
            {
                UnsubscribeProvider(removedProvider);
            }

            MetricsProviders[provider.GetType()] = provider;
            SubscribeProvider(provider);
        }
    }

    /// <summary>
    /// Unregisters a previously registered provider instance and detaches its event handlers.
    /// </summary>
    /// <param name="provider">The provider instance to remove.</param>
    public static void Unregister(IMongoDbClientMetricProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        lock (MetricsProviders)
        {
            foreach (var entry in MetricsProviders)
            {
                if (ReferenceEquals(entry.Value, provider) && MetricsProviders.TryRemove(entry.Key, out _))
                {
                    UnsubscribeProvider(provider);
                    break;
                }
            }
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
