namespace OpenCode.Sdk.Tests.Support;

/// <summary>One executable cancellation path and the public members whose first boundary it proves.</summary>
internal sealed record ServiceConfigCancellationDoor(
    string Row, IReadOnlyList<string> MemberNames, Func<CancellationToken, Task> Invoke);
