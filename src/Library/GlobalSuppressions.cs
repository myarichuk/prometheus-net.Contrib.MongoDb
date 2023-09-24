// This file is used by Code Analysis to maintain SuppressMessage
// attributes that are applied to this project.
// Project-level suppressions either have no target or are given
// a specific target and scoped to a namespace, type, member, etc.

using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("StyleCop.CSharp.OrderingRules", "SA1202:Elements should be ordered by access", Justification = "Not very important", Scope = "member", Target = "~F:PrometheusNet.Contrib.MongoDb.Handlers.ConnectionMetricsProvider.ConnectionCreationRate")]
[assembly: SuppressMessage("StyleCop.CSharp.MaintainabilityRules", "SA1401:Fields should be private", Justification = "Some fields need to be 'internal' for testing", Scope = "member", Target = "*")]
