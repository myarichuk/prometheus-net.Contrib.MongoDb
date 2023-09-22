﻿namespace PrometheusNet.MongoDb.Events;

public abstract class MongoCommandEvent
{
    public string OperationRawType { get; set; }

    public int RequestId { get; set; }

    public MongoOperationType OperationType { get; set; }

    public string TargetDatabase { get; set; }

    public string TargetCollection { get; set; }

    public Dictionary<string, object> Command { get; set; }

    public TimeSpan? Duration { get; set; }

    public long? CursorId { get; set; }
}