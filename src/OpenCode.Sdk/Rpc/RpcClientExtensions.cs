using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk;

/// <summary>Calls the typed methods a plugin's RPC definition declares.</summary>
public static class RpcClientExtensions
{
    /// <summary>Calls one method of a plugin's RPC definition with typed input and output, as opencode's own client binds a definition.</summary>
    /// <param name="client">The RPC family.</param>
    /// <param name="method">The method's descriptor.</param>
    /// <param name="input">The method's input, serialized with its input metadata; a null input is sent as JSON null.</param>
    /// <param name="location">The location query that selects where the RPC is registered; the server default applies when null.</param>
    /// <param name="requestOptions">Options for the request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <typeparam name="TInput">The method's input type.</typeparam>
    /// <typeparam name="TOutput">The method's output type.</typeparam>
    /// <returns>The output read with its metadata; null when the plugin returns JSON null or no output and that metadata reads null as null.</returns>
    /// <remarks>
    /// The call has no network window unless requestOptions sets one. An absent output reads as JSON null.
    /// Whether null is accepted follows the output metadata: a reference or nullable type reads it;
    /// a value type refuses it unless a custom converter accepts null.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> or <paramref name="method"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="requestOptions"/> selects NoThrow, or the RPC id or method name is <c>.</c> or <c>..</c>
    /// (with ParamName <c>rpcId</c> or <c>method</c>, respectively).
    /// </exception>
    /// <exception cref="OpenCodeRpcException">The RPC registry or the plugin reported a failure.</exception>
    /// <exception cref="OpenCodeApiException">The API returned another error.</exception>
    /// <exception cref="OpenCodeTransportException">Transport failed, or the output does not match its type.</exception>
    /// <exception cref="OperationCanceledException">The caller canceled the call.</exception>
    public static Task<TOutput?> CallAsync<TInput, TOutput>(this RpcClient client, RpcMethod<TInput, TOutput> method,
        TInput input, LocationSelector? location = null, OpenCodeRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(method);
        ErrorBehaviorPolicy.RefuseNoThrow(requestOptions, "Typed RPC calls");
        return new RpcMethodCaller(client).CallAsync(method, input, location, requestOptions, cancellationToken);
    }
}
