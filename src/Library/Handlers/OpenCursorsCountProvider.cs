﻿using System.Collections.Concurrent;
using Prometheus;
using PrometheusNet.MongoDb.Events;
#pragma warning disable SA1118
#pragma warning disable SA1118

// ReSharper disable ComplexConditionExpression
namespace PrometheusNet.MongoDb.Handlers;

/// <summary>
/// Provides functionality to track metrics related to MongoDB cursors.
/// Implements the <see cref="IMetricProvider"/> interface.
/// </summary>
public class OpenCursorsCountProvider : IMetricProvider, IDisposable
{
    private const int CursorTimeoutMilliseconds = 1000 * 60 * 2; // 2 min

    private readonly ConcurrentDictionary<long, (int DocumentCount, DateTime LastUpdated, string Collection, string Database)> _cursorDocumentCount = new();

    public readonly Gauge OpenCursors = Metrics.CreateGauge(
        "mongodb_client_open_cursors_count",
        "Number of open cursors",
        new GaugeConfiguration
        {
            LabelNames = new[] { "target_collection", "target_db" },
        });

    public readonly Summary CursorDocumentCount = Metrics.CreateSummary(
        "mongodb_client_cursor_document_count",
        "Count of all documents fetched by a cursor (all batches)",
        new SummaryConfiguration
        {
            LabelNames = new[] { "target_collection", "target_db" },
        });

    private readonly CancellationTokenSource _cts = new();

    public OpenCursorsCountProvider()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, _) =>
        {
            _cts.Cancel();
            _cts.Dispose();
        };

        // Start a task to clean up old entries every minute
        Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                CleanUpOldEntries();
                await Task.Delay(TimeSpan.FromMinutes(1), _cts.Token);
            }
        });
    }

    private void CleanUpOldEntries()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in _cursorDocumentCount)
        {
            _cts.Token.ThrowIfCancellationRequested();
            if (now - entry.Value.LastUpdated > TimeSpan.FromMilliseconds(CursorTimeoutMilliseconds))
            {
                if (_cursorDocumentCount.TryRemove(entry.Key, out var dcd))
                {
                    OpenCursors
                        .WithLabels(dcd.Collection, dcd.Database)
                        .Dec();
                }
            }
        }
    }

    public void Handle(MongoCommandEventStart e)
    {
    }

    public void Handle(MongoCommandEventSuccess e)
    {
        if (e.OperationType is MongoOperationType.Find or MongoOperationType.GetMore or MongoOperationType.Aggregate)
        {
            if (e.OperationType is MongoOperationType.Find or MongoOperationType.Aggregate)
            {
                OpenCursors
                    .WithLabels(e.TargetCollection, e.TargetDatabase)
                    .Inc();
            }

            if (TryGetDocumentCountFromReply(e.Reply, out var documentCount))
            {
                if (TryGetCursorId(e.Reply, out var cursorId))
                {
                    if (cursorId == 0)
                    {
                        // note: if something is wrong, fail fast
                        cursorId = e.CursorId ?? 0;
                    }

                    IncrementDocumentCount(e, cursorId, documentCount);
                }
            }

            // final batch done -> cursor will close
            if (IsFinalBatch(e.Reply) && long.TryParse(e.TargetCollection, out var fetchedCursorId))
            {
                IncrementCursorDocumentCountMetrics(fetchedCursorId, e.TargetCollection, e.TargetDatabase);
                DecrementOpenCursors(e);
            }
        }
    }

    private void DecrementOpenCursors(MongoCommandEventSuccess e) =>
        OpenCursors
            .WithLabels(e.TargetCollection, e.TargetDatabase)
            .Dec();

    private void IncrementCursorDocumentCountMetrics(long cursorId, string targetCollection, string targetDatabase)
    {
        if (_cursorDocumentCount.TryRemove(cursorId, out var rdci))
        {
            CursorDocumentCount
                .WithLabels(targetCollection, targetDatabase)
                .Observe(rdci.DocumentCount);
        }
    }

    private void IncrementDocumentCount(MongoCommandEventSuccess e, long cursorId, int documentCount)
    {
        if (IsFirstBatch(e.Reply))
        {
            _cursorDocumentCount.TryAdd(cursorId,
                (documentCount, DateTime.UtcNow, e.TargetCollection, e.TargetDatabase));
        }
        else
        {
            _cursorDocumentCount.TryUpdate(
                cursorId,
                newValue: (
                    _cursorDocumentCount.TryGetValue(cursorId, out var fetched) ?
                        fetched.DocumentCount + documentCount : documentCount,
                    DateTime.UtcNow,
                    e.TargetCollection,
                    e.TargetDatabase),
                comparisonValue: _cursorDocumentCount.TryGetValue(cursorId, out var comparison) ? 
                    comparison : default);
        }
    }

    public void Handle(MongoCommandEventFailure e)
    {
        // failure means cursor won't be open anymore
        if (e.OperationType is MongoOperationType.Find or MongoOperationType.GetMore)
        {
            OpenCursors
                .WithLabels(e.TargetCollection, e.TargetDatabase)
                .Dec();

            // if there is a failure, record what we have so far
            IncrementCursorDocumentCountMetrics(e.CursorId ?? 0, e.TargetCollection, e.TargetDatabase);
        }
    }

    private static bool TryGetCursorId(Dictionary<string, object> commandReply, out long cursorId)
    {
        cursorId = 0;
        if (commandReply.TryGetValue("cursor", out var cursorAsObject) &&
            cursorAsObject is Dictionary<string, object> cursor)
        {
            if (cursor.TryGetValue("id", out var cursorIdAsObject))
            {
                if (cursorIdAsObject is not long cursorIdValue)
                {
                    return false;
                }

                cursorId = cursorIdValue;
                return true;
            }
        }

        return false;
    }

    private static bool IsFinalBatch(Dictionary<string, object> commandReply)
    {
        if (commandReply.TryGetValue("cursor", out var cursorAsObject) &&
            cursorAsObject is Dictionary<string, object> cursor)
        {
            if (cursor.TryGetValue("id", out var cursorIdAsObject) &&
                cursorIdAsObject is long cursorId)
            {
                return cursorId == 0;
            }
        }

        return false;
    }

    private static bool IsFirstBatch(Dictionary<string, object> commandReply)
    {
        if (commandReply.TryGetValue("cursor", out var cursorAsObject) &&
            cursorAsObject is Dictionary<string, object> cursor)
        {
            return cursor.ContainsKey("firstBatch");
        }

        return false;
    }

    private static bool TryGetDocumentCountFromReply(Dictionary<string, object> commandReply, out int documentCount)
    {
        documentCount = 0;

        if (commandReply.TryGetValue("cursor", out var cursorAsObject) &&
            cursorAsObject is Dictionary<string, object> cursor)
        {
            if (cursor.TryGetValue("firstBatch", out var batchDocumentAsObject) &&
                batchDocumentAsObject is object[] firstBatchDocuments)
            {
                documentCount = firstBatchDocuments.Length;
                return true;
            }

            if (cursor.TryGetValue("nextBatch", out var nextBatchDocumentAsObject) &&
                nextBatchDocumentAsObject is object[] nextBatchDocuments)
            {
                documentCount = nextBatchDocuments.Length;
                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
