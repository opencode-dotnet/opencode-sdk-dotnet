using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>Serves deterministic HTTP/1.1 responses through the platform's real HTTP handler.</summary>
internal sealed class LoopbackHttpServer : IAsyncDisposable
{
    private const string ContentLengthPrefix = "Content-Length:";

    private readonly Task _acceptLoop;
    private readonly TaskCompletionSource<object?> _clientDisconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentBag<TcpClient> _clients = [];
    private readonly ConcurrentBag<Task> _connections = [];
    private readonly TcpListener _listener;
    private readonly ConcurrentQueue<LoopbackRequest> _requests = [];
    private readonly Func<string, LoopbackHttpResponse> _respond;
    private readonly CancellationTokenSource _shutdown = new();

    private LoopbackHttpServer(Func<string, LoopbackHttpResponse> respond)
    {
        _respond = respond;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var localEndpoint = (IPEndPoint)_listener.LocalEndpoint;
        Endpoint = new Uri($"http://127.0.0.1:{localEndpoint.Port.ToString(CultureInfo.InvariantCulture)}");
        _acceptLoop = AcceptLoopAsync();
    }

    public Uri Endpoint { get; }

    public Task ClientDisconnected => _clientDisconnected.Task;

    public IReadOnlyList<string> RequestPaths => [.. _requests.Select(static request => request.Path)];

    /// <summary>Gets the requests the platform handler actually put on the socket, bodies included.</summary>
    public IReadOnlyList<LoopbackRequest> Requests => [.. _requests];

    public static LoopbackHttpServer Start(Func<string, LoopbackHttpResponse> respond)
    {
        ArgumentNullException.ThrowIfNull(respond);
        return new LoopbackHttpServer(respond);
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelOnWorkerAsync();
        _listener.Stop();
        Task[] acceptTasks = [_acceptLoop];
        await Task.WhenAll(acceptTasks);
        ReleaseResponses();
        await Task.WhenAll(_connections);
#if NET
        _listener.Dispose();
#endif
        _shutdown.Dispose();
    }

    public void ReleaseResponses()
    {
        foreach (var client in _clients)
        {
            client.Close();
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync();
            }
            catch (Exception exception) when (_shutdown.IsCancellationRequested && exception is ObjectDisposedException or SocketException)
            {
                break;
            }

            _clients.Add(client);
            _connections.Add(ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                // Taking the stream belongs to the exchange too: the accept loop records the
                // client before it starts serving it, so a concurrent ReleaseResponses can close
                // the socket first, and GetStream then refuses the disposed client.
                using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream);
                if (request is null)
                {
                    // The peer opened the connection and closed it before writing a request line:
                    // a client whose own bound fired first (the probe's request timeout under a
                    // loaded machine), not a handler that wrote something wrong.
                    _ = _clientDisconnected.TrySetResult(null);
                    return;
                }

                _requests.Enqueue(request);
                var response = _respond(request.Path);
                await WriteResponseAsync(stream, response);
                if (response.KeepOpen)
                {
                    await WaitForClientDisconnectAsync(stream);
                    _ = _clientDisconnected.TrySetResult(null);
                }
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException)
            {
                // The peer aborting mid-exchange, or ReleaseResponses closing the socket under an
                // in-flight write, is connection teardown rather than a fixture failure; DisposeAsync
                // awaits this task, so a propagated fault would fail the test from teardown. A torn
                // connection is also the disconnect signal the kept-open fixtures await.
                _ = _clientDisconnected.TrySetResult(null);
            }
        }
    }

    private static async Task WaitForClientDisconnectAsync(Stream stream)
    {
        var buffer = new byte[1];
        try
        {
            while (await ReadAsync(stream, buffer) > 0)
            {
                // A GET request has no body; ignore any unexpected bytes until the client closes.
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException)
        {
            _ = exception;
        }
    }

    /// <summary>
    /// Reads the request head and, when the head declares one, exactly the declared body, so a
    /// test can assert what the platform handler really wrote rather than what it was handed.
    /// Returns null when the peer closed the connection before writing anything.
    /// </summary>
    private static async Task<LoopbackRequest?> ReadRequestAsync(Stream stream)
    {
        using var reader = new StreamReader(
            stream,
            Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        var requestLine = await reader.ReadLineAsync();
        if (requestLine is null)
        {
            return null;
        }

        var contentLength = 0;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var header = await reader.ReadLineAsync();
        while (!string.IsNullOrEmpty(header))
        {
            if (header.StartsWith(ContentLengthPrefix, StringComparison.OrdinalIgnoreCase))
            {
                contentLength = int.Parse(
                    header[ContentLengthPrefix.Length..].Trim(),
                    CultureInfo.InvariantCulture);
            }

            var separator = header.IndexOf(':', StringComparison.Ordinal);
            if (separator > 0)
            {
                headers[header[..separator].Trim()] = header[(separator + 1)..].Trim();
            }

            header = await reader.ReadLineAsync();
        }

        var parts = requestLine.Split(' ');
        if (parts.Length < 2)
        {
            throw new InvalidOperationException($"Invalid loopback request line '{requestLine}'.");
        }

        var body = string.Empty;
        if (contentLength > 0)
        {
            var buffer = new char[contentLength];
            var read = 0;
            while (read < contentLength)
            {
                var count = await reader.ReadAsync(buffer, read, contentLength - read);
                if (count is 0)
                {
                    break;
                }

                read += count;
            }

            body = new string(buffer, 0, read);
        }

        return new LoopbackRequest(parts[0], parts[1], body, headers);
    }

    private static async Task WriteResponseAsync(Stream stream, LoopbackHttpResponse response)
    {
        var body = Encoding.UTF8.GetBytes(response.Body);
        var headers = new StringBuilder()
            .Append("HTTP/1.1 ")
            .Append(((int)response.StatusCode).ToString(CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(ReasonPhrase(response.StatusCode))
            .Append("\r\n");
        if (response.ContentType is not null)
        {
            _ = headers.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
        }

        if (response.Location is not null)
        {
            _ = headers.Append("Location: ").Append(response.Location).Append("\r\n");
        }

        if (response.KeepOpen)
        {
            _ = headers.Append("Transfer-Encoding: chunked\r\nConnection: keep-alive\r\n\r\n");
        }
        else
        {
            _ = headers.Append("Content-Length: ")
                .Append(body.Length.ToString(CultureInfo.InvariantCulture))
                .Append("\r\nConnection: close\r\n\r\n");
        }

        var headerBytes = Encoding.ASCII.GetBytes(headers.ToString());
        await WriteAsync(stream, headerBytes);
        if (response.KeepOpen)
        {
            var chunkPrefix = Encoding.ASCII.GetBytes(body.Length.ToString("X", CultureInfo.InvariantCulture) + "\r\n");
            await WriteAsync(stream, chunkPrefix);
            await WriteAsync(stream, body);
            await WriteAsync(stream, [13, 10]);
        }
        else
        {
            await WriteAsync(stream, body);
        }

        await stream.FlushAsync();
    }

    private static string ReasonPhrase(HttpStatusCode statusCode) => (int)statusCode switch
    {
        200 => "OK",
        302 => "Found",
        401 => "Unauthorized",
        500 => "Internal Server Error",
        _ => statusCode.ToString(),
    };

    private static Task WriteAsync(Stream stream, byte[] content)
    {
#if NET
        return stream.WriteAsync(content).AsTask();
#else
        return stream.WriteAsync(content, 0, content.Length);
#endif
    }

    private static Task<int> ReadAsync(Stream stream, byte[] buffer)
    {
#if NET
        return stream.ReadAsync(buffer).AsTask();
#else
        return stream.ReadAsync(buffer, 0, buffer.Length);
#endif
    }
}
