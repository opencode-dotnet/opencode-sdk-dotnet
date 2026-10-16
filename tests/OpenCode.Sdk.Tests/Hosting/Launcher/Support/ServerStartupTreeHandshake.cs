using System.Diagnostics;
using System.IO.Abstractions;
using System.Net;
using System.Net.Sockets;

namespace OpenCode.Sdk.Tests.Hosting.Launcher.Support;

/// <summary>
/// Owns independent root and child control leases, authenticated and observed live before ACK.
/// Neither lease is released by a successful handshake or a launcher's failure result.
/// </summary>
internal sealed class ServerStartupTreeHandshake : IDisposable
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

    private TcpListener? _listener;
    private ServerStartupTreeLease? _child;
    private ServerStartupTreeLease? _root;
    private readonly IFileSystem _fileSystem;

    public ServerStartupTreeHandshake(IFileSystem fileSystem) => _fileSystem = fileSystem;

    public Process ChildProcess => Child.Process;

    public Process RootProcess => Root.Process;

    public string Nonce { get; private set; } = string.Empty;

    public int Port { get; private set; }

    private TcpListener Listener =>
        _listener ?? throw new InvalidOperationException("The handshake listener has not been started.");

    private ServerStartupTreeLease Child =>
        _child ?? throw new InvalidOperationException("The child lease has not been captured.");

    private ServerStartupTreeLease Root =>
        _root ?? throw new InvalidOperationException("The root lease has not been captured.");

    public void Start()
    {
        Nonce = Guid.NewGuid().ToString("N");
        _listener = new TcpListener(IPAddress.Loopback, 0);
        Listener.Start();
        Port = ((IPEndPoint)Listener.LocalEndpoint).Port;
    }

    public async Task CaptureAndAcknowledgeAsync(bool acknowledge, CancellationToken cancellationToken)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(HandshakeTimeout);
        _root = new ServerStartupTreeLease(await AcceptTcpClientAsync(bound.Token), _fileSystem);
        try
        {
            var childPid = await Root.AuthenticateAsync(Nonce, "root", bound.Token);
            _child = new ServerStartupTreeLease(await AcceptTcpClientAsync(bound.Token), _fileSystem);
            _ = await Child.AuthenticateAsync(Nonce, "child", bound.Token);
            if (RootProcess.Id == ChildProcess.Id || childPid != ChildProcess.Id
                || RootProcess.HasExited || ChildProcess.HasExited)
            {
                throw new InvalidOperationException("The handshake did not identify the live root and its child.");
            }

            if (!acknowledge)
            {
                throw new InvalidOperationException(
                    "The child-tree handshake was intentionally rejected for cleanup verification.");
            }

            await Child.AcknowledgeAsync(bound.Token);
            await Root.AcknowledgeAsync(bound.Token);
        }
        catch
        {
            // Keep the child lease open: pre-ACK rejection must prove the root reaps its own
            // child, without child-lease fallback supplying the result.
            Root.Release();
            throw;
        }
    }

    public void Stop() => _listener?.Stop();

    public void ReleaseChild() => _child?.Release();

    public void ReleaseRoot() => _root?.Release();

    public async Task<string> DescribeProcessesAsync(CancellationToken cancellationToken) =>
        $"Root: {await Root.DescribeProcessAsync(cancellationToken)}; "
        + $"child: {await Child.DescribeProcessAsync(cancellationToken)}";

    public Task WaitForChildExitAsync(CancellationToken cancellationToken) =>
        _child?.WaitForExitAsync(cancellationToken) ?? Task.CompletedTask;

    public Task WaitForRootExitAsync(CancellationToken cancellationToken) =>
        _root?.WaitForExitAsync(cancellationToken) ?? Task.CompletedTask;

    public void Dispose()
    {
        _child?.Dispose();
        _root?.Dispose();
        _listener?.Stop();
#if NET
        _listener?.Dispose();
#endif
    }

    private Task<TcpClient> AcceptTcpClientAsync(CancellationToken cancellationToken)
    {
#if NET
        return Listener.AcceptTcpClientAsync(cancellationToken).AsTask();
#else
        return Listener.AcceptTcpClientAsync().WaitAsync(cancellationToken);
#endif
    }
}
