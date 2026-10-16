using System.Diagnostics;
using System.Globalization;
using System.IO.Abstractions;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace OpenCode.Sdk.Tests.Hosting.Launcher.Support;

/// <summary>
/// Owns one authenticated fixture connection and its process observation. Releasing the connection
/// asks that peer to exit; teardown never signals a PID that could have been recycled.
/// </summary>
internal sealed class ServerStartupTreeLease : IDisposable
{
    private readonly TcpClient _client;
    private readonly IFileSystem _fileSystem;
    private Process? _process;
    private string? _observedLinuxState;

    public ServerStartupTreeLease(TcpClient client, IFileSystem fileSystem)
    {
        _client = client;
        _fileSystem = fileSystem;
    }

    public Process Process =>
        _process ?? throw new InvalidOperationException("The fixture process has not been captured.");

    public async Task<int?> AuthenticateAsync(string nonce, string role, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            _client.GetStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024, leaveOpen: true);
#if NET
        var line = await reader.ReadLineAsync(cancellationToken);
#else
        var line = await reader.ReadLineAsync().WaitAsync(cancellationToken);
#endif
        using var document = JsonDocument.Parse(line
            ?? throw new InvalidOperationException("The fixture connection ended before identity arrived."));
        var identity = document.RootElement;
        if (!string.Equals(identity.GetProperty("nonce").GetString(), nonce, StringComparison.Ordinal)
            || !string.Equals(identity.GetProperty("role").GetString(), role, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The fixture connection did not match this scenario and role.");
        }

        _process = Process.GetProcessById(identity.GetProperty("pid").GetInt32());
        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
        {
            // Windows retains this native handle on the Process until disposal. Unix Process.Handle
            // is not an OS process-identity handle and is deliberately not used as one.
            _ = _process.Handle;
        }

        if (_process.HasExited)
        {
            throw new InvalidOperationException("The authenticated fixture process was not alive when captured.");
        }

        _observedLinuxState = await ReadLinuxStateAsync(cancellationToken);

        return identity.TryGetProperty("childPid", out var childPid) ? childPid.GetInt32() : null;
    }

    public async Task AcknowledgeAsync(CancellationToken cancellationToken)
    {
        var stream = _client.GetStream();
        var acknowledgement = Encoding.ASCII.GetBytes("ACK\n");
#if NET
        await stream.WriteAsync(acknowledgement, cancellationToken);
#else
        await stream.WriteAsync(acknowledgement, 0, acknowledgement.Length, cancellationToken);
#endif
        await stream.FlushAsync(cancellationToken);
    }

    public void Release() => _client.Dispose();

    public async Task<string> DescribeProcessAsync(CancellationToken cancellationToken) =>
        $"PID {Process.Id.ToString(CultureInfo.InvariantCulture)}; observed /proc stat: {_observedLinuxState}; "
        + $"current /proc stat: {await ReadLinuxStateAsync(cancellationToken)}";

    public async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        if (_process is { } process)
        {
            // On Unix this is a conservative PID observation only: reuse can fail/timeout this
            // check, but cannot authorize killing the replacement. The lease owns cleanup.
            await process.WaitForExitAsync(cancellationToken);
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _process?.Dispose();
    }

    private async Task<string> ReadLinuxStateAsync(CancellationToken cancellationToken)
    {
#if NET
        if (!OperatingSystem.IsLinux())
#else
        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Linux))
#endif
        {
            return "unavailable on this OS";
        }

        try
        {
            // One bounded read, never a retry or cleanup authority. Linux stat includes state,
            // PPID and start ticks, so failure evidence can distinguish a zombie from a live
            // survivor or a recycled PID by comparing this with the pre-ACK snapshot.
            using var stream = _fileSystem.File.OpenText(
                "/proc/" + Process.Id.ToString(CultureInfo.InvariantCulture) + "/stat");
            var buffer = new char[4096];
#if NET
            var count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
#else
            var count = await stream.ReadAsync(buffer, 0, buffer.Length).WaitAsync(cancellationToken);
#endif
            return new string(buffer, 0, count).Trim();
        }
        catch (IOException exception)
        {
            return exception.Message;
        }
        catch (UnauthorizedAccessException exception)
        {
            return exception.Message;
        }
        catch (OperationCanceledException exception)
        {
            return exception.Message;
        }
    }
}
