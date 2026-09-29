using BenchmarkDotNet.Running;
using PrometheusNet.MongoDb.Benchmarks;

BenchmarkSwitcher.FromAssembly(typeof(MongoBenchmarks).Assembly).Run(args);
