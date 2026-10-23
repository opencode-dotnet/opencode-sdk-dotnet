using System.Diagnostics;
using System.Globalization;
using System.IO.Abstractions;
using System.Net;
using System.Net.Sockets;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Hosting.Launcher.Support;

/// <summary>
/// Owns independent root and child control leases, authenticated and observed live before ACK.
/// Neither lease is released by a successful handshake or a launcher's failure result.
/// </summary>
internal sealed class ServerStartupTreeHandshake : IDisposable
{
    /// <summary>How long the root and its child have to connect back, authenticate and be acknowledged.</summary>
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

    private TcpListener? _listener;
    private ServerStartupTreeLease? _child;
    private ServerStartupTreeLease? _root;
    private readonly IFileSystem _fileSystem;
    private readonly TimeProvider _time;

    public ServerStartupTreeHandshake(IFileSystem fileSystem)
        : this(fileSystem, TimeProvider.System)
    {
    }

    /// <summary>Creates a handshake whose bound runs on <paramref name="time"/>.</summary>
    /// <param name="fileSystem">The file system the leases read process details from.</param>
    /// <param name="time">The clock that drives <see cref="HandshakeTimeout"/>.</param>
    public ServerStartupTreeHandshake(IFileSystem fileSystem, TimeProvider time)
    {
        _fileSystem = fileSystem;
        _time = time;
    }

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

    /// <summary>
    /// Accepts and authenticates the root, then its child, and acknowledges both. When
    /// <see cref="HandshakeTimeout"/> runs out first, the failure is a <see cref="TimeoutException"/>
    /// naming the bound and the step it was waiting for; the caller's own cancellation stays an
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <param name="acknowledge">False rejects the tree before the acknowledgement, for cleanup verification.</param>
    /// <param name="cancellationToken">The caller's cancellation.</param>
    /// <returns>A task that completes once both leases are acknowledged.</returns>
    public async Task CaptureAndAcknowledgeAsync(bool acknowledge, CancellationToken cancellationToken)
    {
        using var deadline = TimedCancellation.Create(HandshakeTimeout, _time);
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var step = new HandshakeStep(deadline, cancellationToken);
        _root = new ServerStartupTreeLease(
            await step.WithinBoundAsync(() => AcceptTcpClientAsync(bound.Token), "the root to connect back"), _fileSystem);
        try
        {
            var childPid = await step.WithinBoundAsync(() => Root.AuthenticateAsync(Nonce, "root", bound.Token), "the root to authenticate");
            _child = new ServerStartupTreeLease(
                await step.WithinBoundAsync(() => AcceptTcpClientAsync(bound.Token), "the child to connect back"), _fileSystem);
            _ = await step.WithinBoundAsync(() => Child.AuthenticateAsync(Nonce, "child", bound.Token), "the child to authenticate");
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

            await step.WithinBoundAsync(() => Child.AcknowledgeAsync(bound.Token), "the child's acknowledgement");
            await step.WithinBoundAsync(() => Root.AcknowledgeAsync(bound.Token), "the root's acknowledgement");
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

#if NET
    private Task<TcpClient> AcceptTcpClientAsync(CancellationToken cancellationToken) =>
        Listener.AcceptTcpClientAsync(cancellationToken).AsTask();
#else
    /// <summary>
    /// .NET Framework's accept takes no token, so cancellation stops the listener, and the accept
    /// itself is awaited until it ends: no accept outlives the handshake that started it.
    /// </summary>
    private async Task<TcpClient> AcceptTcpClientAsync(CancellationToken cancellationToken)
    {
        var listener = Listener;
        using var stop = cancellationToken.Register(static state => ((TcpListener)state).Stop(), listener);
        try
        {
            return await listener.AcceptTcpClientAsync();
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested
            && exception is ObjectDisposedException or SocketException or InvalidOperationException)
        {
            throw new OperationCanceledException("The accept was canceled.", exception, cancellationToken);
        }
    }
#endif

    /// <summary>
    /// Turns a cancellation that only <see cref="HandshakeTimeout"/> caused into a
    /// <see cref="TimeoutException"/> naming the step it was waiting for.
    /// </summary>
    private readonly struct HandshakeStep(CancellationTokenSource deadline, CancellationToken caller)
    {
        public async Task<T> WithinBoundAsync<T>(Func<Task<T>> step, string waitingFor)
        {
            try
            {
                return await step();
            }
            catch (OperationCanceledException exception) when (TimedOut)
            {
                throw Timeout(waitingFor, exception);
            }
        }

        public async Task WithinBoundAsync(Func<Task> step, string waitingFor)
        {
            try
            {
                await step();
            }
            catch (OperationCanceledException exception) when (TimedOut)
            {
                throw Timeout(waitingFor, exception);
            }
        }

        private bool TimedOut => deadline.IsCancellationRequested && !caller.IsCancellationRequested;

        private static TimeoutException Timeout(string waitingFor, OperationCanceledException exception) =>
            new(
                "The child-tree handshake did not complete within "
                + HandshakeTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)
                + " s while waiting for " + waitingFor + ".",
                exception);
    }
}
