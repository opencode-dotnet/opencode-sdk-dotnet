using Microsoft.CodeAnalysis.Text;

namespace OpenCode.Sdk.Tools.Tests.Support;

internal sealed record CSharpSourceFile(string Path, SourceText Text, IReadOnlyList<CSharpSourceRegion> Regions);
