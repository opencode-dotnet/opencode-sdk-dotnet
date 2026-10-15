namespace OpenCode.Sdk.Extensions.Tests.Support;

/// <summary>Canned configuration sections for the binding tests.</summary>
internal static class OpenCodeConfigurationData
{
    /// <summary>A password-protected server with a non-default username.</summary>
    public static IReadOnlyDictionary<string, string?> ProtectedServer { get; } =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["OpenCode:Endpoint"] = "http://localhost:4096",
            ["OpenCode:Username"] = "admin",
            ["OpenCode:Password"] = "secret",
        };

    /// <summary>A server section that sets the event bus's idle window to the given text.</summary>
    public static IReadOnlyDictionary<string, string?> WithEventStreamIdleTimeout(string value) =>
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["OpenCode:Endpoint"] = "http://localhost:4096",
            ["OpenCode:EventStreamIdleTimeout"] = value,
        };
}
