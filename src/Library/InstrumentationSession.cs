using System.Collections.Concurrent;
using System.Diagnostics;
using MongoDB.Driver.Core.Configuration;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Events;
using PrometheusNet.Contrib.MongoDb;
using PrometheusNet.Contrib.MongoDb.Handlers;
using PrometheusNet.MongoDb.Events;
using PrometheusNet.MongoDb.Handlers;

namespace PrometheusNet.MongoDb;

/// <summary>
/// The instrumentation of one client: its options, its metrics, the providers that record
/// them, and the state that correlates a command's start with its end. Created by
/// <see cref="MongoInstrumentation.InstrumentForPrometheus"/>
/// and kept alive by the driver's event subscriptions, so nothing here is static: clients
/// (and tests) never share state.
/// </summary>
internal sealed class InstrumentationSession : ICollectSource
{
    // Keyed by (connection, request): request ids are scoped per connection, so a bare
    // request id can collide across concurrent connections and miscorrelate metrics.
    private readonly ConcurrentDictionary<(ConnectionId Connection, int Request), CommandCorrelation> _commands = new();

    private readonly CollectionLabels _labels;

    private readonly CounterChild _orphanedCommands;

    private readonly IMongoDbClientMetricProvider[] _providers;

    private readonly ISweepingProvider[] _sweepingProviders;

    private readonly bool _measureRequestSize;

    private readonly bool _publishFilterDocument;

    public InstrumentationSession(MongoInstrumentationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Options = options;
        Sink = new MetricSink(options.Registry, options.Meter, options.ClientLabel);
        Hub = new EventHub(Sink.Counter(
            "mongodb_client_instrumentation_handler_errors_total",
            "Exceptions thrown by metric providers while handling instrumentation events",
            "provider"));
        _labels = new CollectionLabels(options, Sink);
        _orphanedCommands = Sink
            .Counter(
                "mongodb_client_instrumentation_orphaned_commands_total",
                "Commands whose end event never arrived within the orphan timeout; they are dropped unmeasured")
            .WithLabels();

        var providers = new List<IMongoDbClientMetricProvider>
        {
            new CommandDurationMetricProvider(this),
            new CommandErrorsMetricProvider(this),
            new CommandRetryMetricProvider(this),
            new QueryCountMetricProvider(this),
            new CursorMetricsProvider(this),
            new ConnectionMetricsProvider(this),
            new ConnectionPoolMetricsProvider(this),
            new ClusterMetricsProvider(this),
        };

        if (options.MeasureSizes)
        {
            providers.Add(new CommandRequestSizeMetricProvider(this));
            providers.Add(new CommandResponseSizeProvider(this));
        }

        if (options.MeasureFilterComplexity)
        {
            providers.Add(new QueryFilterSizeMetricProvider(this));
        }

        providers.AddRange(options.Providers);
        _providers = providers.ToArray();
        _sweepingProviders = _providers.OfType<ISweepingProvider>().ToArray();

        // Sizing a command is cheap but not free; skip it when nobody reads the size.
        _measureRequestSize = options.MeasureSizes || options.Providers.Count > 0;

        // Reading the filter out of a wire-encoded command materializes it; only the filter
        // complexity metric and custom providers look at it.
        _publishFilterDocument = options.MeasureFilterComplexity || options.Providers.Count > 0;

        foreach (var provider in _providers)
        {
            ProviderWiring.Subscribe(Hub, provider);
        }

        Sink.OnCollect(this);
    }

    public MongoInstrumentationOptions Options { get; }

    public MetricSink Sink { get; }

    public EventHub Hub { get; }

    /// <summary>
    /// How long a command may wait for its end event before it counts as orphaned. Generous,
    /// because a command without <c>maxTimeMS</c> (an index build, a large aggregation) can
    /// legitimately run for a long time, and sweeping it early would lose its metrics.
    /// </summary>
    internal TimeSpan OrphanTimeout { get; set; } = TimeSpan.FromHours(1);

    internal int InFlightCommands => _commands.Count;

    internal double OrphanedCommands => _orphanedCommands.Value;

    /// <summary>The session's provider of the given type (built-in or custom).</summary>
    internal T Provider<T>()
        where T : class, IMongoDbClientMetricProvider =>
        _providers.OfType<T>().FirstOrDefault()
        ?? throw new InvalidOperationException($"{typeof(T).Name} is not enabled on this client");

    /// <summary>Subscribes the session to a cluster's driver events.</summary>
    public void Attach(ClusterBuilder cb)
    {
        cb.Subscribe<CommandStartedEvent>(OnCommandStarted);
        cb.Subscribe<CommandSucceededEvent>(OnCommandSucceeded);
        cb.Subscribe<CommandFailedEvent>(OnCommandFailed);

        // Connection, pool, server selection and monitoring events need no correlation:
        // the driver's structs go to the providers as they are. Most are raised by an
        // operation, or by pool maintenance on its behalf (see TraceContext.Live); server
        // monitoring never is, so its metrics carry no exemplars.
        var hub = Hub;
        cb.Subscribe(TraceContext.Live<ConnectionOpenedEvent>(hub.Publish));
        cb.Subscribe(TraceContext.Live<ConnectionClosedEvent>(hub.Publish));
        cb.Subscribe(TraceContext.Live<ConnectionFailedEvent>(hub.Publish));
        cb.Subscribe(TraceContext.Live<ConnectionPoolCheckedOutConnectionEvent>(hub.Publish));
        cb.Subscribe(TraceContext.Live<ConnectionPoolCheckingOutConnectionFailedEvent>(hub.Publish));
        cb.Subscribe(TraceContext.Live<ConnectionPoolCheckedInConnectionEvent>(hub.Publish));
        cb.Subscribe(TraceContext.Live<ConnectionPoolAddedConnectionEvent>(hub.Publish));
        cb.Subscribe(TraceContext.Live<ConnectionPoolRemovedConnectionEvent>(hub.Publish));
        cb.Subscribe(TraceContext.Live<ConnectionPoolClearedEvent>(hub.Publish));
        cb.Subscribe(TraceContext.Live<ClusterSelectedServerEvent>(hub.Publish));
        cb.Subscribe(TraceContext.Live<ClusterSelectingServerFailedEvent>(hub.Publish));
        cb.Subscribe(TraceContext.Detached<ServerHeartbeatFailedEvent>(hub.Publish));
        cb.Subscribe(TraceContext.Detached<ServerDescriptionChangedEvent>(hub.Publish));
    }

    /// <summary>Runs before each collection: sweeps stale state, reports commands in flight.</summary>
    public int Collect(long nowTimestamp)
    {
        SweepOrphanedCommands(nowTimestamp);
        foreach (var provider in _sweepingProviders)
        {
            provider.Sweep(nowTimestamp);
        }

        return _commands.Count;
    }

    /// <summary>
    /// Drops commands whose end event never arrived, so the correlation map stays bounded.
    /// </summary>
    internal void SweepOrphanedCommands(long nowTimestamp)
    {
        var timeoutTicks = (long)(OrphanTimeout.TotalSeconds * Stopwatch.Frequency);
        foreach (var (key, correlation) in _commands)
        {
            // Removes only this exact entry: a live command reusing the key must survive.
            if (nowTimestamp - correlation.StartedTimestamp > timeoutTicks
                && ((ICollection<KeyValuePair<(ConnectionId, int), CommandCorrelation>>)_commands).Remove(new(key, correlation)))
            {
                _orphanedCommands.Inc();
            }
        }
    }

    /// <summary>Back-dates an in-flight command, so tests can orphan it without waiting.</summary>
    internal bool AgeCommandForTests(ConnectionId connection, int requestId, TimeSpan age)
    {
        var key = (connection, requestId);
        return _commands.TryGetValue(key, out var correlation)
            && _commands.TryUpdate(key, correlation with { StartedTimestamp = correlation.StartedTimestamp - (long)(age.TotalSeconds * Stopwatch.Frequency) }, correlation);
    }

    internal void OnCommandStarted(CommandStartedEvent e)
    {
        if (MongoInstrumentation.IsIgnored(e.CommandName))
        {
            return;
        }

        var command = e.Command;
        var operationType = MongoInstrumentation.GetOperationType(e.CommandName);

        // Commands without a collection (commitTransaction, listDatabases, `aggregate: 1`, ...)
        // are measured with an empty collection label.
        var (database, collection) = _labels.Resolve(
            e.DatabaseNamespace?.DatabaseName ?? MongoInstrumentation.GetDatabase(command),
            MongoInstrumentation.GetCollection(e.CommandName, operationType, command));

        // Exact serialized size: the buffer length of a wire-encoded command, a structural
        // sum for a materialized one; no buffer is ever materialized (see BsonSizeCounter).
        var requestSize = _measureRequestSize ? BsonSizeCounter.GetSizeInBytes(command) : 0;
        var filterDocument = _publishFilterDocument ? MongoInstrumentation.GetFilterDocument(command, operationType) : null;

        // Each lookup on a wire-encoded command re-reads it from the start, and a miss reads
        // all of it, so only the fields this command type can carry are looked up.
        var correlation = new CommandCorrelation(
            database,
            collection,
            operationType,
            requestSize,
            operationType == MongoOperationType.GetMore ? MongoInstrumentation.GetCursorId(e.CommandName, command) : null,
            operationType == MongoOperationType.KillCursors ? MongoInstrumentation.GetKilledCursorIds(e.CommandName, command) : null,
            MongoInstrumentation.CursorNeverTimesOut(command, operationType, filterDocument),
            Stopwatch.GetTimestamp());

        // Last-writer-wins: a stale orphan under the same key must never shadow a live command.
        _commands[(e.ConnectionId, e.RequestId)] = correlation;

        var commandEvent = EventPool<MongoCommandEventStart>.Rent();
        try
        {
            commandEvent.RequestId = e.RequestId;
            commandEvent.OperationId = e.OperationId ?? 0;
            commandEvent.OperationRawType = e.CommandName;
            commandEvent.CommandDocument = command;
            commandEvent.RawRequestSizeInBytes = requestSize;
            commandEvent.Duration = null; // no duration yet
            commandEvent.OperationType = operationType;
            commandEvent.TargetDatabase = database;
            commandEvent.TargetCollection = collection;
            commandEvent.FilterDocument = filterDocument;
            commandEvent.CursorId = correlation.CursorId;
            commandEvent.KilledCursorIds = correlation.KilledCursorIds;
            commandEvent.CursorNeverTimesOut = correlation.CursorNeverTimesOut;
            commandEvent.ServerEndPoint = e.ConnectionId?.ServerId?.EndPoint;
            Hub.Publish(commandEvent);
        }
        finally
        {
            EventPool<MongoCommandEventStart>.Return(commandEvent);
        }
    }

    internal void OnCommandSucceeded(CommandSucceededEvent e)
    {
        if (MongoInstrumentation.IsIgnored(e.CommandName) || !_commands.TryRemove((e.ConnectionId, e.RequestId), out var correlation))
        {
            return;
        }

        var commandEvent = EventPool<MongoCommandEventSuccess>.Rent();
        try
        {
            Populate(commandEvent, correlation, e.CommandName, e.RequestId, e.OperationId, e.Duration, e.ConnectionId);
            commandEvent.ReplyDocument = e.Reply;
            MongoInstrumentation.ExtractCursorInfo(e.Reply, commandEvent);
            Hub.Publish(commandEvent);
        }
        finally
        {
            EventPool<MongoCommandEventSuccess>.Return(commandEvent);
        }
    }

    internal void OnCommandFailed(CommandFailedEvent e)
    {
        if (MongoInstrumentation.IsIgnored(e.CommandName) || !_commands.TryRemove((e.ConnectionId, e.RequestId), out var correlation))
        {
            return;
        }

        // Note: the start command document is NOT retained. The driver recycles its
        // buffers after sending, so touching it here would throw ObjectDisposedException.
        // End events therefore carry scalars only; document views are start-event-only.
        var commandEvent = EventPool<MongoCommandEventFailure>.Rent();
        try
        {
            Populate(commandEvent, correlation, e.CommandName, e.RequestId, e.OperationId, e.Duration, e.ConnectionId);
            commandEvent.Failure = e.Failure;
            Hub.Publish(commandEvent);
        }
        finally
        {
            EventPool<MongoCommandEventFailure>.Return(commandEvent);
        }
    }

    private static void Populate(
        MongoCommandEvent target,
        in CommandCorrelation correlation,
        string commandName,
        int requestId,
        long? operationId,
        TimeSpan duration,
        ConnectionId? connectionId)
    {
        target.RequestId = requestId;
        target.OperationId = operationId ?? 0;
        target.OperationRawType = commandName;
        target.RawRequestSizeInBytes = correlation.RequestSizeInBytes;
        target.Duration = duration;
        target.OperationType = correlation.OperationType;
        target.TargetDatabase = correlation.Database;
        target.TargetCollection = correlation.Collection;
        target.CursorId = correlation.CursorId;
        target.KilledCursorIds = correlation.KilledCursorIds;
        target.CursorNeverTimesOut = correlation.CursorNeverTimesOut;
        target.ServerEndPoint = connectionId?.ServerId?.EndPoint;
    }

    /// <summary>
    /// Minimal per-command correlation state. Deliberately small: entries live in
    /// <see cref="_commands"/> only between the driver's start and end callbacks, and
    /// orphans (commands whose end event never arrives) stay cheap until
    /// <see cref="SweepOrphanedCommands"/> drops them.
    /// No command or reply payloads are retained here.
    /// <see cref="KilledCursorIds"/> and <see cref="CursorNeverTimesOut"/> are carried over
    /// because cursor providers act on them when the reply arrives, and by then the command
    /// document is gone.
    /// </summary>
    private readonly record struct CommandCorrelation(
        string Database,
        string Collection,
        MongoOperationType OperationType,
        int RequestSizeInBytes,
        long? CursorId,
        List<long>? KilledCursorIds,
        bool CursorNeverTimesOut,
        long StartedTimestamp);
}

/// <summary>A provider with state to sweep each time metrics are collected.</summary>
internal interface ISweepingProvider
{
    void Sweep(long nowTimestamp);
}
