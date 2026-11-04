using System.Text;
using System.Text.RegularExpressions;
using OpenCode.Sdk;
using OpenCode.Sdk.Models;

namespace OpenCode.Playground.Hq.Features.Terminal;

/// <summary>One shell for a session's page, opened in the session's folder.</summary>
internal sealed partial class SessionTerminal(OpenCodeClient client, ILogger<SessionTerminal> logger)
{
    private const int OutputLimit = 20_000;
    private readonly StringBuilder _output = new();
    private LocationSelector? _location;
    private PtyClient? _pty;
    private PtySession? _session;
    private int _opening;

    public event EventHandler? Changed;
    public bool IsOpen => _session is not null;
    public bool IsOpening => Volatile.Read(ref _opening) == 1;
    public string? LastError { get; private set; }
    public string Output
    {
        get
        {
            lock (_output)
            {
                return _output.ToString();
            }
        }
    }

    /// <summary>Opens a shell in the session's folder and reads its output until it ends or the token is cancelled.</summary>
    public async Task OpenAsync(string sessionId, CancellationToken cancellationToken)
    {
        // One open at a time: a second click while the first is pending would create a PTY nothing removes.
        if (_session is not null || Interlocked.Exchange(ref _opening, 1) == 1)
        {
            return;
        }

        LastError = null;
        try
        {
            var session = (await client.Sessions.GetSessionClient(sessionId).GetAsync(cancellationToken: cancellationToken)).Session;
            _location = new LocationSelector { Directory = session.Location.Directory };
            #region panel: terminal
            var created = await client.Ptys.CreatePtyAsync(new PtyCreateRequest { Title = "HQ", Location = _location }, cancellationToken: cancellationToken);
            _pty = client.Ptys.GetPtyClient(created.Pty.Id);
            _session = await _pty.ConnectAsync(new PtyConnectOptions { Location = _location }, cancellationToken);
            #endregion
            // This terminal owns the read: every end closes it, cancellation quietly; faults reach ReadFaulted.
            // The inline continuation only logs, sets a string and raises Changed, whose sole subscriber queues
            // renderer work, so its discarded task has no fault of its own to observe.
            _ = ReadAsync(_session, cancellationToken).ContinueWith(
                ReadFaulted, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (OpenCodeException exception)
        {
            logger.LogWarning(exception, "Could not open a terminal for session {SessionId}.", sessionId);
            LastError = $"The terminal did not open: {exception.Message}";
            await CloseAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The page left while opening: remove a PTY created after its own close found nothing.
            await CloseAsync();
            return;
        }
        finally
        {
            Volatile.Write(ref _opening, 0);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Runs one line in the shell.</summary>
    public async Task SendAsync(string line, CancellationToken cancellationToken)
    {
        if (_session is not { } session)
        {
            return;
        }

        try
        {
            await session.SubmitAsync(line, cancellationToken);
        }
        catch (OpenCodeException exception)
        {
            logger.LogWarning(exception, "Could not send a line to a terminal.");
            LastError = $"The line was not sent: {exception.Message}";
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (ObjectDisposedException)
        {
            // The shell ended while this line waited: the SDK refuses a send on a disposed connection.
            LastError = "The line was not sent: the terminal has closed.";
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Ends the connection and removes the PTY from the server once; a failed remove is reported, not thrown.</summary>
    public async Task CloseAsync()
    {
        // Take both before disposing: disposal wakes the read loop, whose own close must find nothing left to take.
        var session = Interlocked.Exchange(ref _session, null);
        var pty = Interlocked.Exchange(ref _pty, null);
        try
        {
            if (session is not null)
            {
                await session.DisposeAsync();
            }
        }
        finally
        {
            if (pty is not null)
            {
                try
                {
                    _ = await pty.RemoveAsync(new PtyRemoveRequest { Location = _location });
                }
                catch (OpenCodeException exception)
                {
                    logger.LogWarning(exception, "Could not remove a terminal; it lives until the opencode server stops.");
                    LastError = $"The terminal was not removed: {exception.Message} It lives until the opencode server stops.";
                }
            }
        }
    }

    private async Task ReadAsync(PtySession session, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in session.ReadAsync(cancellationToken))
            {
                if (frame is PtyOutputFrame output)
                {
                    Append(output.Text);
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }

            // A read that CloseAsync ended was ended by HQ, not by the shell.
            if (_session is not null)
            {
                LastError = "The shell exited.";
            }
        }
        catch (OpenCodeException exception)
        {
            logger.LogWarning(exception, "A terminal stopped.");
            LastError = $"The terminal stopped: {exception.Message}";
        }
        finally
        {
            await CloseAsync();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Reports an unexpected read failure after the terminal has closed, so its cause takes precedence over a remove failure.
    /// </summary>
    private void ReadFaulted(Task read)
    {
        logger.LogWarning(read.Exception, "A terminal's read failed.");
        LastError = $"The terminal stopped: {read.Exception?.GetBaseException().Message}";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Keeps the text and drops colour and cursor control sequences, since a <c>pre</c> is not a terminal emulator.</summary>
    private void Append(string text)
    {
        lock (_output)
        {
            _ = _output.Append(ControlSequence().Replace(text, string.Empty));
            if (_output.Length > OutputLimit)
            {
                _ = _output.Remove(0, _output.Length - OutputLimit);
            }
        }
    }

    // Shell output is untrusted input: the pattern is linear, and the timeout still bounds a match.
    [GeneratedRegex(@"\x1B(\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(\x07|\x1B\\)|[0-Z\\^-~])", RegexOptions.NonBacktracking | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ControlSequence();
}
