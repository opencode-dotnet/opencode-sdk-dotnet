namespace OpenCode.Sdk.Tests.Harness;

/// <summary>
/// A <c>tool.cancel</c> notification: the backend removed the pending simulated-tool invocation
/// because its execution was interrupted, and any later settlement of that id is refused.
/// </summary>
internal sealed record DriveToolCancellation(string Id, string Reason);
