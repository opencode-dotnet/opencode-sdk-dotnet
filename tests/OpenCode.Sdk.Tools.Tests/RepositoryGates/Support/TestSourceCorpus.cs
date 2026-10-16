using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.RepositoryGates.Support;

internal sealed record TestSourceCorpus(IReadOnlyList<string> Paths, IReadOnlyList<CSharpSourceFile> Files);
