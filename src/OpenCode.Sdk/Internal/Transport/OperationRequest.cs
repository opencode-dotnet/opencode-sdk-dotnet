using System.Text.Json.Serialization.Metadata;

namespace OpenCode.Sdk.Internal;

/// <summary>The immutable call facts shared by the one-shot plane and every stream attempt.</summary>
internal sealed record OperationRequest(string OperationId, OperationTraits Traits, HttpMethod Method, string Route, object? Body,
    JsonTypeInfo? BodyTypeInfo, Stream? Content, OpenCodeRequestOptions? Options, IReadOnlyList<DeclaredHeader>? DeclaredHeaders)
{
    public static OperationRequest Describe<TBody>(string operationId, HttpMethod method, string route, TBody? body,
        JsonTypeInfo<TBody>? bodyTypeInfo, Stream? content, OpenCodeRequestOptions? options, IReadOnlyList<DeclaredHeader>? declaredHeaders)
        where TBody : class =>
        new(operationId, OperationTraits.Lookup(operationId), method, route, body, bodyTypeInfo, content, SnapshotOptions(options), declaredHeaders);

    private static OpenCodeRequestOptions? SnapshotOptions(OpenCodeRequestOptions? options)
    {
        if (options?.Headers is not { } headers)
        {
            return options;
        }

        return new OpenCodeRequestOptions
        {
            Headers = RequestDecorationPolicy.SnapshotHeaders(headers),
            Location = options.Location,
            NetworkTimeout = options.NetworkTimeout,
            ErrorBehavior = options.ErrorBehavior,
        };
    }

    public void Validate()
    {
        ValidateArguments(Method, Route);

        static void ValidateArguments(HttpMethod method, string route)
        {
            ArgumentNullException.ThrowIfNull(method);
            ArgumentException.ThrowIfNullOrWhiteSpace(route);

            if (route[0] is not '/')
            {
                throw new ArgumentException("Routes must start with '/'.", nameof(route));
            }
        }
    }
}
