namespace OpenCode.Sdk.Internal;

/// <summary>Resolves the per-call error channel and refuses it for doors that always throw.</summary>
internal static class ErrorBehaviorPolicy
{
    public static ErrorBehavior Resolve(OpenCodeRequestOptions? options)
    {
        var errorBehavior = options?.ErrorBehavior ?? ErrorBehavior.Default;
        if (errorBehavior is not (ErrorBehavior.Default or ErrorBehavior.NoThrow))
        {
            throw new ArgumentOutOfRangeException(nameof(options), errorBehavior, "Unknown ErrorBehavior value.");
        }

        return errorBehavior;
    }

    public static void RefuseNoThrow(OpenCodeRequestOptions? options, string doors)
    {
        if (Resolve(options) is ErrorBehavior.NoThrow)
        {
            throw new ArgumentException($"{doors} always throw API errors; NoThrow is not supported.", nameof(options));
        }
    }
}
