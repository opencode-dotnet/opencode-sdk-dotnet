using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace OpenCode.Sdk;

/// <summary>Declares one application tool; arguments are not validated against its schema.</summary>
[Experimental("OPENCODE001", UrlFormat = "https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/experimental.md#opencode001")]
public sealed record ClientMcpTool
{
    /// <summary>Gets the tool name; the first declaration with a matching name answers a call.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the description, omitted when unset.</summary>
    public string? Description { get; init; }

    /// <summary>Gets manual schema fields; caller fields override the default object type.</summary>
    /// <remarks>The connection clones this value at admission, before its document can end.</remarks>
    public JsonElement? InputSchema { get; init; }

    /// <summary>Gets application work, which may overlap other calls and is neither canceled nor joined on disposal.</summary>
    public required Func<JsonElement, ClientMcpToolContext, Task<ClientMcpToolResult>> Handler { get; init; }
}
