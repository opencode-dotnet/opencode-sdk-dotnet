namespace OpenCode.Sdk.Tools.Clones;

/// <summary>The C# files under <c>src/</c>, split by the generator's manifest, as repository-relative paths with forward slashes.</summary>
internal sealed record CloneSourceSplit(IReadOnlyList<string> Generated, IReadOnlyList<string> HandWritten);
