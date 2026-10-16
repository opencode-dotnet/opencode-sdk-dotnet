namespace OpenCode.Sdk.Tests.Support;

/// <summary>Bounds a hang while waiting for an event; it decides no verdict.</summary>
internal static class HangGuard
{
    public static TimeSpan Bound { get; } = TimeSpan.FromSeconds(30);
}
