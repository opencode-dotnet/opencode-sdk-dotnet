using System.Text.Json;
using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Internal;

/// <summary>The explicit connection's ordered error policy, independent of transport and clocks.</summary>
/// <remarks>
/// Pipeline status refusals and <see cref="ResponseAdapter{OpenCodeResponse}.NullSuccessBodyMessage"/>
/// carry no inner transport cause. Decoder failures carry JsonException; network failures retain their transport cause.
/// </remarks>
internal static class ExplicitProbeFailureClassifier
{
    public static Exception Map(Exception failure, Uri endpoint, bool hasPassword, CancellationToken caller, CancellationToken bound)
    {
        if (caller.IsCancellationRequested)
        {
            return FailureClassification.CallerCancellation(failure, failure.Message, caller);
        }

        if (failure is OpenCodeApiException { Error: UnauthorizedError })
        {
            return new OpenCodeServerException(hasPassword
                ? $"Server at {endpoint} rejected the password"
                : $"Server at {endpoint} requires a password; set OpenCodeServerResolveOptions.Password", failure);
        }

        if (failure is OperationCanceledException && bound.IsCancellationRequested)
        {
            return new OpenCodeServerException($"Could not reach server at {endpoint}: {failure.Message}", failure);
        }

        if (failure is OpenCodeTransportException { InnerException: { } cause } && cause is not JsonException)
        {
            return new OpenCodeServerException($"Could not reach server at {endpoint}: {cause.Message}", failure);
        }

        return new OpenCodeServerException($"Server at {endpoint} did not provide a compatible V2 health response: {failure.Message}", failure);
    }
}
