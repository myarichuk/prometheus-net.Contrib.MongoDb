using System.Collections.Concurrent;
using PrometheusNet.Contrib.MongoDb.Events;
using PrometheusNet.MongoDb.Events;
using PrometheusNet.MongoDb.Handlers;

// ReSharper disable CollectionNeverQueried.Local

namespace PrometheusNet.MongoDb;

internal static class MetricProviderRegistrar
{
    private static readonly string[] ExcludedAssemblyPrefixes =
    {
        "System.", "Microsoft.", "mscorlib",
    };

    private static bool _isRegistered;

    // just in case, for testing mostly
    static MetricProviderRegistrar() => RegisterAll();

    // needed to prevent GC from collecting metric providers
    private static readonly ConcurrentDictionary<Type, IMongoDbClientMetricProvider> MetricsProviders = new();

    public static void ReplaceForTests<TProvider>(TProvider newProvider)
        where TProvider : class, IMongoDbClientMetricProvider
    {
        if (MetricsProviders.TryRemove(typeof(TProvider), out var removedProvider))
        {
            EventHub.Default.Unsubscribe<MongoCommandEventStart>(removedProvider.Handle);
            EventHub.Default.Unsubscribe<MongoCommandEventFailure>(removedProvider.Handle);
            EventHub.Default.Unsubscribe<MongoCommandEventSuccess>(removedProvider.Handle);
            EventHub.Default.Unsubscribe<MongoConnectionOpenedEvent>(removedProvider.Handle);
            EventHub.Default.Unsubscribe<MongoConnectionClosedEvent>(removedProvider.Handle);
            EventHub.Default.Unsubscribe<MongoConnectionFailedEvent>(removedProvider.Handle);
        }

        if (MetricsProviders.TryAdd(typeof(TProvider), newProvider))
        {
            EventHub.Default.Subscribe<MongoCommandEventStart>(newProvider.Handle);
            EventHub.Default.Subscribe<MongoCommandEventFailure>(newProvider.Handle);
            EventHub.Default.Subscribe<MongoCommandEventSuccess>(newProvider.Handle);
            EventHub.Default.Subscribe<MongoConnectionOpenedEvent>(newProvider.Handle);
            EventHub.Default.Subscribe<MongoConnectionClosedEvent>(newProvider.Handle);
            EventHub.Default.Subscribe<MongoConnectionFailedEvent>(newProvider.Handle);
        }
    }

    public static void RegisterAll()
    {
        lock (MetricsProviders)
        {
            if (_isRegistered)
            {
                return;
            }

            _isRegistered = true;
        }

        foreach (var metricProviderType in EnumerateIMetricsHandlers())
        {
            var metricProvider = (IMongoDbClientMetricProvider)Activator.CreateInstance(metricProviderType);
            MetricsProviders.TryAdd(metricProviderType, metricProvider);

            EventHub.Default.Subscribe<MongoCommandEventStart>(metricProvider.Handle);
            EventHub.Default.Subscribe<MongoCommandEventFailure>(metricProvider.Handle);
            EventHub.Default.Subscribe<MongoCommandEventSuccess>(metricProvider.Handle);
            EventHub.Default.Subscribe<MongoConnectionOpenedEvent>(metricProvider.Handle);
            EventHub.Default.Subscribe<MongoConnectionClosedEvent>(metricProvider.Handle);
            EventHub.Default.Subscribe<MongoConnectionFailedEvent>(metricProvider.Handle);
        }
    }

    public static bool TryGetProvider<TProvider>(out TProvider? provider) where TProvider : class, IMongoDbClientMetricProvider
    {
        var success = MetricsProviders.TryGetValue(typeof(TProvider), out var providerAsObject);
        return success ?
            (provider = (TProvider)providerAsObject) != null :
            (provider = null) != null;
    }

    private static IEnumerable<Type> EnumerateIMetricsHandlers()
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach (var assembly in assemblies.Where(x => !x.IsDynamic && File.Exists(x.Location)))
        {
            if (ExcludedAssemblyPrefixes.Any(prefix => 
                    assembly.FullName.StartsWith(
                        prefix, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // the location might not exist for some assemblies, so check that too
            if (File.Exists(assembly.Location))
            {
                foreach (var type in assembly.GetTypes())
                {
                    if (typeof(IMongoDbClientMetricProvider).IsAssignableFrom(type) && !type.IsInterface)
                    {
                        yield return type;
                    }
                }
            }
        }
    }
}