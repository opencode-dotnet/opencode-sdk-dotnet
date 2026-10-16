namespace OpenCode.Sdk.Internal;

/// <summary>Resolves the per-call error channel and refuses it for doors that always throw.</summary>
internal static class ErrorBehaviorPolicy
{
    public static ErrorBehavior Resolve(OpenCodeRequestOptions? requestOptions)
    {
        var errorBehavior = requestOptions?.ErrorBehavior ?? ErrorBehavior.Default;
        if (errorBehavior is not (ErrorBehavior.Default or ErrorBehavior.NoThrow))
        {
            throw new ArgumentOutOfRangeException(nameof(requestOptions), errorBehavior, "Unknown ErrorBehavior value.");
        }

        return errorBehavior;
    }

    public static void RefuseNoThrow(OpenCodeRequestOptions? requestOptions, string doors)
    {
        if (Resolve(requestOptions) is ErrorBehavior.NoThrow)
        {
            throw new ArgumentException($"{doors} always throw API errors; NoThrow is not supported.", nameof(requestOptions));
        }
    }
}
