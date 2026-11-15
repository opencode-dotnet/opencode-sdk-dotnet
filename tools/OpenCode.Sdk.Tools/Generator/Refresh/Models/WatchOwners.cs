namespace OpenCode.Sdk.Tools.Generator.Refresh.Models;

/// <summary>The products that depend on watched upstream behaviors.</summary>
internal static class WatchOwners
{
    public const string Sdk = "opencode-sdk-dotnet";

    public const string AgentsAi = "opencode-agents-ai";

    public static readonly IReadOnlyList<string> All = Array.AsReadOnly([Sdk, AgentsAi]);
}
