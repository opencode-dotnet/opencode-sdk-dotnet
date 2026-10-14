namespace OpenCode.Sdk;

/// <summary>Selects how a single call surfaces opencode API error responses.</summary>
public enum ErrorBehavior
{
    /// <summary>API error responses throw <see cref="OpenCodeApiException"/>.</summary>
    Default = 0,

    /// <summary>API error responses return on the response envelope instead of throwing.</summary>
    NoThrow = 1,
}
