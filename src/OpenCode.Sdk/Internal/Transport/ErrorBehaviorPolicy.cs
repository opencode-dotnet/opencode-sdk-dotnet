namespace OpenCode.Sdk.Internal;

/// <summary>Resolves the per-call error channel and refuses unsupported NoThrow requests.</summary>
internal static class ErrorBehaviorPolicy
{
    public const string EventSubscriptionNoThrowMessage =
        "Event subscriptions report ordinary attempt failures through connection state; NoThrow is not supported.";

    public static ErrorBehavior Resolve(OpenCodeRequestOptions? requestOptions)
    {
        var errorBehavior = requestOptions?.ErrorBehavior ?? ErrorBehavior.Default;
        if (errorBehavior is not (ErrorBehavior.Default or ErrorBehavior.NoThrow))
        {
            throw new ArgumentOutOfRangeException(nameof(requestOptions), errorBehavior, "Unknown ErrorBehavior value.");
        }

        return errorBehavior;
    }

    public static void RefuseNoThrow(OpenCodeRequestOptions? requestOptions, string doors, string? message = null)
    {
        if (Resolve(requestOptions) is ErrorBehavior.NoThrow)
        {
            throw new ArgumentException(message ?? $"{doors} always throw API errors; NoThrow is not supported.", nameof(requestOptions));
        }
    }
}
