using System.Text.Json;

namespace OpenCode.Sdk.Internal;

/// <summary>An independently owned JSON object, or an ignored frame with an undefined root.</summary>
internal sealed record ClientMcpMessage(JsonElement Root);
