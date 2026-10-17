namespace OpenCode.Sdk.Tests.Common.Support;

public sealed record DeclaredErrorDoor(
    string MemberName,
    string OperationId,
    string HttpMethod,
    string Path,
    Func<OpenCodeClient, OpenCodeRequestOptions?, Task<OpenCodeResponse>> Invoke);
