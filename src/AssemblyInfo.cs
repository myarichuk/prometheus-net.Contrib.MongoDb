using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// see https://docs.microsoft.com/en-us/dotnet/fundamentals/code-analysis/quality-rules/ca5393
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]

// replace Library.Tests with actual test project name
[assembly: InternalsVisibleTo("Library.Tests, PublicKey=00240000048000009400000006020000002400005253413100040000010001004de0d2adac4b8c06917474204100a67d4bd7554852c2a25b2d10c639a54230cc6cb58c37d804a27d5d9f59931f8b2bdd2c7da9c97837263b1ea39c5ac1e7d7b7df4a30c20756da57cf7f761a5b680e8444436de2ae18192c5d365b904977321b48d097c4139a2fa8486ddea6ee647b75b92748fa1174bc7de34197cc1c48acb7")]

[assembly: SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1633:File must have header", Justification = "Not relevant")]
[assembly: SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1649:FileNameMustMatchTypeName", Justification = "Not relevant")]
[assembly: SuppressMessage("StyleCop.CSharp.ReadabilityRules", "SA1101:PrefixLocalCallsWithThis", Justification = "Not relevant")]
[assembly: SuppressMessage("StyleCop.CSharp.NamingRules", "SA1309:FieldNamesMustNotBeginWithUnderscore", Justification = "Reviewed.")]
[assembly: SuppressMessage("StyleCop.CSharp.OrderingRules", "SA1210:UsingDirectivesMustBeOrderedAlphabeticallyByNamespace", Justification = "Reviewed.")]
[assembly: SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1629:DocumentationTextMustEndWithAPeriod", Justification = "Reviewed.")]
