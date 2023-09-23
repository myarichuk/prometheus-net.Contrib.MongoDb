using System.Collections.Concurrent;
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

    // needed to prevent GC from collecting metric providers
    private static readonly ConcurrentBag<IMetricProvider> MetricsProviders = new();

    public static void RegisterAll()
    {
        foreach (var metricProviderType in EnumerateIMetricsHandlers())
        {
            var metricProvider = (IMetricProvider)Activator.CreateInstance(metricProviderType);
            MetricsProviders.Add(metricProvider);

            EventHub.Default.Subscribe<MongoCommandEventStart>(metricProvider.Handle);
            EventHub.Default.Subscribe<MongoCommandEventFailure>(metricProvider.Handle);
            EventHub.Default.Subscribe<MongoCommandEventSuccess>(metricProvider.Handle);
        }
    }

    public static bool TryGetProvider<TProvider>(out TProvider provider) where TProvider : class, IMetricProvider
    {
        provider = MetricsProviders.FirstOrDefault(p => p is TProvider) as TProvider;
        return provider != null;
    }

    private static IEnumerable<Type> EnumerateIMetricsHandlers()
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach (var assembly in assemblies.Where(x => File.Exists(x.Location)))
        {
            if (ExcludedAssemblyPrefixes.Any(prefix => 
                    assembly.FullName.StartsWith(
                        prefix, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            foreach (var type in assembly.GetTypes())
            {
                if (typeof(IMetricProvider).IsAssignableFrom(type) && !type.IsInterface)
                {
                    yield return type;
                }
            }
        }
    }
}