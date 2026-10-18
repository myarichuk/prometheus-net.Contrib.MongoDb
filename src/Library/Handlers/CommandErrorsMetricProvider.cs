using System.Collections.Frozen;
using System.Net.Sockets;
using MongoDB.Driver;
using PrometheusNet.MongoDb.Events;

// ReSharper disable UnusedMember.Global

namespace PrometheusNet.MongoDb.Handlers;

/// <summary>
/// Counts failed commands. Besides the exception type, each failure is labelled with what
/// an alert needs to act on: the server's error code name, a coarse category, and whether
/// the driver may retry it.
/// </summary>
internal sealed class CommandErrorsMetricProvider : IMongoDbClientMetricProvider
{
    internal const string Timeout = "timeout";
    internal const string Network = "network";
    internal const string Server = "server";
    internal const string Client = "client";

    private const int MaxTimeMSExpired = 50;

    // The codes the driver retries a read or write on (retryable reads and writes spec):
    // network errors reported by a mongos, primary changes and shutdowns.
    private static readonly FrozenSet<int> RetryableCodes = FrozenSet.ToFrozenSet(
    [
        6, // HostUnreachable
        7, // HostNotFound
        89, // NetworkTimeout
        91, // ShutdownInProgress
        134, // ReadConcernMajorityNotAvailableYet
        189, // PrimarySteppedDown
        262, // ExceededTimeLimit
        9001, // SocketException
        10107, // NotWritablePrimary
        11600, // InterruptedAtShutdown
        11602, // InterruptedDueToReplStateChange
        13435, // NotPrimaryNoSecondaryOk
        13436, // NotPrimaryOrSecondary
    ]);

    private readonly MetricChildCache<(string Command, Type Error, string CodeName, string Category, bool Retryable, string Collection, string Database), CounterChild> _errorsCache;

    public CommandErrorsMetricProvider(InstrumentationSession session)
    {
        CommandErrors = session.Sink.Counter(
            "mongodb_client_command_errors_total",
            "Failed commands, by error type, server code name, category and retryability",
            "command_type",
            "error_type",
            "code_name",
            "category",
            "retryable",
            "target_collection",
            "target_db");
        _errorsCache = new(key => CommandErrors.WithLabels(
            key.Command,
            key.Error.Name,
            key.CodeName,
            key.Category,
            key.Retryable ? "true" : "false",
            key.Collection,
            key.Database));
    }

    /// <summary>
    /// Failed commands by command, exception type (<c>error_type</c>), server code name
    /// (<c>code_name</c>, empty when the server did not reply), <c>category</c>
    /// (timeout, network, server or client) and <c>retryable</c>.
    /// </summary>
    internal CounterFamily CommandErrors { get; }

    public void Handle(MongoCommandEventFailure e)
    {
        var failure = e.Failure;
        _errorsCache
            .Get((e.OperationRawType, failure.GetType(), CodeName(failure), Category(failure), IsRetryable(failure), e.TargetCollection, e.TargetDatabase))
            .Inc();
    }

    // Code names are a fixed server vocabulary (DuplicateKey, NotWritablePrimary, ...),
    // unlike messages, so they are safe as a label.
    internal static string CodeName(Exception failure) =>
        failure is MongoCommandException { Result: not null } command ? command.CodeName ?? string.Empty : string.Empty;

    internal static string Category(Exception failure) => failure switch
    {
        TimeoutException or MongoExecutionTimeoutException => Timeout,
        _ when ServerCode(failure) == MaxTimeMSExpired => Timeout,
        MongoConnectionException or IOException or SocketException => Network,
        MongoServerException => Server,
        _ => Client,
    };

    /// <summary>
    /// Whether the failure is transient: the driver retries it (a network error, or one of
    /// the codes retryable reads and writes retry on), the server labelled it
    /// <c>RetryableWriteError</c>, or the whole transaction can be retried
    /// (<c>TransientTransactionError</c>).
    /// </summary>
    internal static bool IsRetryable(Exception failure) => failure switch
    {
        MongoConnectionException => true,
        MongoException mongo when mongo.HasErrorLabel("RetryableWriteError") || mongo.HasErrorLabel("TransientTransactionError") => true,
        _ => ServerCode(failure) is { } code && RetryableCodes.Contains(code),
    };

    /// <summary>
    /// The server's error code, or null when there is none. A <see cref="MongoCommandException"/>
    /// built without a reply throws from <see cref="MongoCommandException.Code"/>, so the
    /// reply is checked first.
    /// </summary>
    internal static int? ServerCode(Exception? failure) =>
        failure is MongoCommandException { Result: not null } command ? command.Code : null;
}
