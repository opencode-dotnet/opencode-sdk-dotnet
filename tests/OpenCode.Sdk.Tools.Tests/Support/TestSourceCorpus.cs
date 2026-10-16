namespace OpenCode.Sdk.Tools.Tests.Support;

internal sealed record TestSourceCorpus(IReadOnlyList<string> Paths, IReadOnlyList<CSharpSourceFile> Files);
