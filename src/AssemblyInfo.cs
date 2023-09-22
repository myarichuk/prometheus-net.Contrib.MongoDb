using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

// see https://docs.microsoft.com/en-us/dotnet/fundamentals/code-analysis/quality-rules/ca5393
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]

[assembly: SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1633:File must have header", Justification = "Not relevant")]
[assembly: SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1649:FileNameMustMatchTypeName", Justification = "Not relevant")]
[assembly: SuppressMessage("StyleCop.CSharp.ReadabilityRules", "SA1101:PrefixLocalCallsWithThis", Justification = "Not relevant")]
[assembly: SuppressMessage("StyleCop.CSharp.NamingRules", "SA1309:FieldNamesMustNotBeginWithUnderscore", Justification = "Reviewed.")]
[assembly: SuppressMessage("StyleCop.CSharp.OrderingRules", "SA1210:UsingDirectivesMustBeOrderedAlphabeticallyByNamespace", Justification = "Reviewed.")]
[assembly: SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1629:DocumentationTextMustEndWithAPeriod", Justification = "Reviewed.")]
