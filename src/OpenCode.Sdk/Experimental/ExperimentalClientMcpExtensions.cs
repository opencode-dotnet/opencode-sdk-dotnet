using System.Diagnostics.CodeAnalysis;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk;

/// <summary>Exposes application-owned tools to OpenCode as an experimental MCP server.</summary>
[Experimental("OPENCODE001", UrlFormat = "https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/experimental.md#opencode001")]
public static class ExperimentalClientMcpExtensions
{
    /// <summary>Mints a short-lived, single-use browser ticket scoped to the request's name and location query.</summary>
    /// <param name="client">The experimental client providing the root HTTP endpoint and credentials.</param>
    /// <param name="request">The required client name and optional location query.</param>
    /// <param name="requestOptions">The per-call options.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The standard token envelope, including expiry and resolved location.</returns>
    /// <exception cref="OpenCodeApiException">A declared 400, 401, 403 or 404 error, unless NoThrow is selected.</exception>
    /// <exception cref="OpenCodeTransportException">The transport failed or the success body was malformed.</exception>
    /// <remarks>The ticket header is supplied internally. Connecting or reconnecting never calls this door automatically.</remarks>
    public static Task<ExperimentalMcpClientTokenResponse> CreateClientMcpConnectTokenAsync(
        this ExperimentalClient client, ExperimentalMcpClientTokenRequest request,
        OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);
        var pipeline = client.PipelineOrNull ?? throw MockSeam.CreateError(nameof(ExperimentalClient), nameof(CreateClientMcpConnectTokenAsync));
        return new ClientMcpTokensRawClient(pipeline).CreateConnectTokenAsync(
            request, xOpencodeTicket: PtyTicketHeader.Sentinel, requestOptions, cancellationToken);
    }

    /// <summary>Begins a connection immediately; initial factory and setup failures are synchronous.</summary>
    /// <param name="client">The experimental client providing the root endpoint, credential and clock.</param>
    /// <param name="options">The namespace, ordered tools and optional base URL and header overrides.</param>
    /// <returns>A caller-owned reconnecting handle, not a readiness or registration acknowledgment.</returns>
    /// <remarks>An explicit URL receives the root credential unless its handshake headers override Authorization.</remarks>
    public static ClientMcpConnection ConnectClientMcp(this ExperimentalClient client, ClientMcpOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Name is null || options.Tools is null || options.Tools.Any(static tool => tool is null || tool.Name is null || tool.Handler is null))
        {
            throw new ArgumentException("Client MCP name, tools and handlers are required.", nameof(options));
        }

        if (options.Tools.Any(static tool => tool.InputSchema is { ValueKind: not System.Text.Json.JsonValueKind.Object }))
        {
            throw new ArgumentException("Client MCP input schemas must be objects.", nameof(options));
        }

        var pipeline = client.PipelineOrNull ?? throw MockSeam.CreateError(nameof(ExperimentalClient), nameof(ConnectClientMcp));
        var connector = new ClientMcpConnector(pipeline.Connection, options);
        return new ClientMcpConnection(options, pipeline.Connection.Time, connector.Create,
            (socket, token) => connector.UpgradeAsync((ClientTerminalWebSocket)socket, token));
    }
}
