using System.Diagnostics.CodeAnalysis;

namespace OpenCode.Sdk;

/// <summary>Identifies the OpenCode session that invoked an application tool.</summary>
[Experimental("OPENCODE001", UrlFormat = "https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/experimental.md#opencode001")]
public sealed class ClientMcpToolContext
{
    /// <summary>Creates invocation context for an application tool or its unit tests.</summary>
    /// <param name="sessionId">The calling session, or null when no session metadata is available.</param>
    public ClientMcpToolContext(string? sessionId) => SessionId = sessionId;

    /// <summary>Gets session metadata only when its wire value is a string, including an empty string.</summary>
    public string? SessionId { get; }
}
