﻿using PrometheusNet.MongoDb.Events;

namespace PrometheusNet.MongoDb.Handlers;

public interface IMetricProvider
{
    void Handle(MongoCommandEventStart e);

    void Handle(MongoCommandEventSuccess e);

    void Handle(MongoCommandEventFailure e);
}