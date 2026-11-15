using System.Diagnostics.CodeAnalysis;

namespace OpenCode.Sdk;

/// <summary>Returns ordered text/image content and an optional explicit tool-error flag.</summary>
[Experimental("OPENCODE001", UrlFormat = "https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/experimental.md#opencode001")]
public sealed record ClientMcpToolResult
{
    /// <summary>Gets content in application order; collections remain caller-owned shallow references.</summary>
    public required IReadOnlyList<ClientMcpContent> Content { get; init; }

    /// <summary>Gets the error flag; null omits it, while false is emitted explicitly.</summary>
    public bool? IsError { get; init; }
}
