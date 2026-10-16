namespace OpenCode.Sdk.Tests.Harness;

internal sealed record ServerTestFailure
{
    public required Exception Exception { get; init; }

    public required string Test { get; init; }

    public required string Invocation { get; init; }

    public required string Details { get; init; }

    public required string Id { get; init; }

    public bool Reported { get; set; }
}
