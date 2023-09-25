using MongoDB.Driver;
using Prometheus;
using PrometheusNet.MongoDb.Handlers;

namespace PrometheusNet.MongoDb.Tests;

public class TestDocument
{
    public string Id { get; set; }

    public string Name { get; set; }

    public int Age { get; set; }
}
