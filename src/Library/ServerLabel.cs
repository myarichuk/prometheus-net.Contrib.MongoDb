using System.Collections.Concurrent;
using System.Net;

namespace PrometheusNet.MongoDb;

/// <summary>
/// Formats a server end point as a <c>server</c> label value: <c>host:port</c>, the way it
/// appears in a connection string. <see cref="DnsEndPoint.ToString"/> would prefix the
/// address family (<c>Unspecified/host:port</c>). Labels are cached per end point, so the
/// string is built once per server.
/// </summary>
internal static class ServerLabel
{
    private static readonly ConcurrentDictionary<EndPoint, string> Labels = new();

    public static string Of(EndPoint? endPoint) =>
        endPoint is null ? string.Empty : Labels.GetOrAdd(endPoint, Format);

    private static string Format(EndPoint endPoint) => endPoint switch
    {
        DnsEndPoint dns => $"{dns.Host}:{dns.Port}",
        _ => endPoint.ToString() ?? string.Empty, // IPEndPoint is already host:port ([v6]:port)
    };
}
