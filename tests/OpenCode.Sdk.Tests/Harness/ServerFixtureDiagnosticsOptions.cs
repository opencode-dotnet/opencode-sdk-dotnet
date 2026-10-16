using System.IO.Abstractions;
using OpenCode.Sdk.Tests.Harness.Ownership.Abstractions;

namespace OpenCode.Sdk.Tests.Harness;

internal sealed record ServerFixtureDiagnosticsOptions
{
    public required IFileSystem FileSystem { get; init; }

    public required string ResultsDirectory { get; init; }

    public IOwnedOperationDeadline? Deadline { get; init; }

    public bool RetainLogs { get; init; }
}
