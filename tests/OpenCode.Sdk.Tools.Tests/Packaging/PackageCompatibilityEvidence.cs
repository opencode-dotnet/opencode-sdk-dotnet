namespace OpenCode.Sdk.Tools.Tests.Packaging;

internal sealed record PackageCompatibilityEvidence
{
    public required string VersionPrefix { get; init; }

    public required string BaselineVersion { get; init; }

    public required IReadOnlySet<string> GeneratedTypes { get; init; }

    public required IReadOnlyDictionary<string, string> HandWrittenPartials { get; init; }

    public required IReadOnlyDictionary<string, IReadOnlyList<CompatibilitySuppression>> ProjectSuppressions { get; init; }
}
