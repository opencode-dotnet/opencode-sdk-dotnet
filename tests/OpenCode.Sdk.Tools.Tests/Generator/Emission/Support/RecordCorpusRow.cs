namespace OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;

internal sealed record RecordCorpusRow(string Name, string RootType, string? UnionType, byte[] Json, bool IsSeed = false);
