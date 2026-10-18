using System.Text;
using Prometheus;
using PrometheusNet.Contrib.MongoDb;

namespace PrometheusNet.MongoDb.Tests;

internal static class TestMetrics
{
    /// <summary>
    /// A session on a registry of its own, so a test's metrics start at zero and no other
    /// test can move them.
    /// </summary>
    public static InstrumentationSession NewSession(Action<MongoInstrumentationOptions>? configure = null) =>
        new(Options(configure));

    public static MongoInstrumentationOptions Options(Action<MongoInstrumentationOptions>? configure = null)
    {
        var options = new MongoInstrumentationOptions { Registry = Metrics.NewCustomRegistry() };
        configure?.Invoke(options);
        return options;
    }

    /// <summary>The registry's text exposition, as a scrape would read it (before-collect callbacks run).</summary>
    public static async Task<string> ExpositionAsync(this InstrumentationSession session)
    {
        using var stream = new MemoryStream();
        await session.Sink.Registry!.CollectAndExportAsTextAsync(stream, default);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The OpenMetrics exposition, the only format that carries exemplars.</summary>
    public static async Task<string> OpenMetricsAsync(this InstrumentationSession session)
    {
        using var stream = new MemoryStream();
        await session.Sink.Registry!.CollectAndExportAsTextAsync(stream, ExpositionFormat.OpenMetricsText, default);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // Some driver events arrive on background threads (pool maintenance, server monitoring),
    // after the operation that caused them has already returned.
    public static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
