using System.Text.Json;

namespace OpenCode.Sdk.Tests.Harness;

/// <summary>
/// A <c>tool.invocation</c> notification: the pending simulated-tool id the controller settles
/// through update/finish/fail (<see cref="Id"/>), the registered tool name, the cloned input the
/// model supplied, and the executing context — the owning session, its agent, the assistant
/// message, and the model's own tool-call id (<see cref="CallId"/>).
/// </summary>
internal sealed record DriveToolInvocation(
    string Id,
    string Name,
    JsonElement Input,
    string SessionId,
    string Agent,
    string MessageId,
    string CallId);
