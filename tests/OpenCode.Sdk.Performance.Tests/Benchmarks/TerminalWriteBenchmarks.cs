using System.Net.WebSockets;
using System.Text;
using BenchmarkDotNet.Attributes;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Performance.Tests.Support;

namespace OpenCode.Sdk.Performance.Tests.Benchmarks;

/// <summary>
/// The terminal write path of both families over an open connection that swallows what is sent
/// (<see cref="SendSinkTerminalWebSocket"/>): a keystroke, a command and a paste, a submitted line,
/// and for the persistent family a resize. Each row's wire figures describe the message it sends.
/// </summary>
/// <remarks>
/// The normal family sends text as one text message. The persistent family sends a binary frame:
/// a type byte (input 1, control 0), then the viewport's columns and rows as big-endian 16-bit
/// values, then the input; the expected frames are written out here from that layout, not taken
/// from the SDK's encoder. Every send passes the connection's send serialization and its deadline.
/// </remarks>
[MemoryDiagnoser]
public class TerminalWriteBenchmarks : IAsyncDisposable
{
    private const string SubmittedLine = "git status --short";
    private const byte InputType = 1;
    private const byte ControlType = 0;
    private const int Columns = 120;
    private const int Rows = 40;

    private static readonly PersistentPtySeed Seed = new();

    private SendSinkTerminalWebSocket? _ptySocket;
    private PtySession? _pty;
    private SendSinkTerminalWebSocket? _persistentSocket;
    private PersistentPtySession? _persistent;

    public static IEnumerable<WireFixture> PtyWrites() =>
        Inputs().Select(static input => Message(input.Name, input.Bytes) with { InputText = Encoding.UTF8.GetString(input.Bytes) });

    public static IEnumerable<WireFixture> PersistentWrites() =>
        Inputs().Select(static input => Message(input.Name, Frame(InputType, input.Bytes)) with { InputBytes = input.Bytes });

    public static IEnumerable<WireFixture> PtySubmits()
    {
        yield return Message("submit", Encoding.UTF8.GetBytes(SubmittedLine + "\r"));
    }

    public static IEnumerable<WireFixture> PersistentSubmits()
    {
        yield return Message("submit", Frame(InputType, Encoding.UTF8.GetBytes(SubmittedLine + "\r")));
    }

    public static IEnumerable<WireFixture> PersistentResizes()
    {
        yield return Message("resize", Frame(ControlType, []));
    }

    /// <summary>
    /// Opens one session per family and proves each row sends exactly its expected message, once:
    /// a resize to the measured viewport and the same resize again (each is sent), then input
    /// framed with that viewport.
    /// </summary>
    [GlobalSetup]
    public async Task SetupAsync()
    {
        _ptySocket = new SendSinkTerminalWebSocket();
        _pty = new PtySession(_ptySocket, TimeProvider.System);
        _persistentSocket = new SendSinkTerminalWebSocket();
        TerminalSocketCore<PersistentPtyFrame>? core = null;
        try
        {
            core = new TerminalSocketCore<PersistentPtyFrame>(
                _persistentSocket, PersistentPtyFrameDecoder.Instance, PersistentPtyClosePolicy.Instance, typeof(PersistentPtySession), TimeProvider.System);
            _persistent = new PersistentPtySession(core, Seed.Attachment());
            core = null;
        }
        finally
        {
            await (core?.DisposeAsync().AsTask() ?? Task.CompletedTask).ConfigureAwait(false);
        }

        foreach (var resize in PersistentResizes())
        {
            await RequireAsync(_persistentSocket, WebSocketMessageType.Binary, resize, () => ResizePersistentAsync(resize)).ConfigureAwait(false);
            await RequireAsync(_persistentSocket, WebSocketMessageType.Binary, resize, () => ResizePersistentAsync(resize)).ConfigureAwait(false);
        }

        foreach (var write in PtyWrites())
        {
            await RequireAsync(_ptySocket, WebSocketMessageType.Text, write, () => WritePtyAsync(write)).ConfigureAwait(false);
        }

        foreach (var write in PersistentWrites())
        {
            await RequireAsync(_persistentSocket, WebSocketMessageType.Binary, write, () => WritePersistentAsync(write)).ConfigureAwait(false);
        }

        foreach (var submit in PtySubmits())
        {
            await RequireAsync(_ptySocket, WebSocketMessageType.Text, submit, () => SubmitPtyAsync(submit)).ConfigureAwait(false);
        }

        foreach (var submit in PersistentSubmits())
        {
            await RequireAsync(_persistentSocket, WebSocketMessageType.Binary, submit, () => SubmitPersistentAsync(submit)).ConfigureAwait(false);
        }
    }

    /// <summary>The normal family's raw write: UTF-8 encode, one text message.</summary>
    [Benchmark]
    [ArgumentsSource(nameof(PtyWrites))]
    public Task WritePtyAsync(WireFixture message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _pty!.WriteAsync(message.InputText!);
    }

    /// <summary>The normal family's submit: validate the line, append the carriage return, encode, send.</summary>
    [Benchmark]
    [ArgumentsSource(nameof(PtySubmits))]
    public Task SubmitPtyAsync(WireFixture message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _pty!.SubmitAsync(SubmittedLine);
    }

    /// <summary>The persistent family's write: copy the caller's bytes into a viewport-headed binary frame.</summary>
    [Benchmark]
    [ArgumentsSource(nameof(PersistentWrites))]
    public Task WritePersistentAsync(WireFixture message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _persistent!.WriteAsync(message.InputBytes);
    }

    /// <summary>The persistent family's submit: the normal family's line work, then the binary frame.</summary>
    [Benchmark]
    [ArgumentsSource(nameof(PersistentSubmits))]
    public Task SubmitPersistentAsync(WireFixture message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _persistent!.SubmitAsync(SubmittedLine);
    }

    /// <summary>The persistent family's resize: a header-only control frame, and the viewport it sets.</summary>
    [Benchmark]
    [ArgumentsSource(nameof(PersistentResizes))]
    public Task ResizePersistentAsync(WireFixture message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _persistent!.ResizeAsync(Columns, Rows);
    }

    [GlobalCleanup]
    public ValueTask CleanupAsync() => DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        if (_persistent is not null)
        {
            await _persistent.DisposeAsync().ConfigureAwait(false);
        }

        if (_pty is not null)
        {
            await _pty.DisposeAsync().ConfigureAwait(false);
        }

        _persistentSocket?.Dispose();
        _ptySocket?.Dispose();
        GC.SuppressFinalize(this);
    }

    private static async Task RequireAsync(SendSinkTerminalWebSocket socket, WebSocketMessageType type, WireFixture expected, Func<Task> send)
    {
        var before = socket.SentMessages;
        socket.CaptureNext();
        await send().ConfigureAwait(false);
        if (socket.SentMessages != before + 1 || socket.LastMessageType != type || socket.Captured is not { } sent
            || !sent.AsSpan().SequenceEqual(expected.Bytes))
        {
            throw new InvalidOperationException($"The '{expected.Name}' row did not send exactly its expected {type} message, once.");
        }
    }

    private static IEnumerable<(string Name, byte[] Bytes)> Inputs()
    {
        yield return ("keystroke", "l"u8.ToArray());
        yield return ("command", "ls -la --color=never\r"u8.ToArray());
        yield return ("paste-16k", Encoding.UTF8.GetBytes(Paste(16 * 1024)));
    }

    /// <summary>A persistent frame as the server reads it: type, columns and rows big-endian, then the data.</summary>
    private static byte[] Frame(byte type, byte[] data) =>
        [type, Columns >> 8, Columns & 0xFF, Rows >> 8, Rows & 0xFF, .. data];

    private static WireFixture Message(string name, byte[] bytes) => new(name, bytes, items: 1, payloadBytesPerItem: bytes.Length);

    /// <summary>A pasted block of ordinary source lines, the size a large clipboard paste reaches.</summary>
    private static string Paste(int characters)
    {
        const string line = "    var total = items.Sum(item => item.Price * item.Quantity);\r";
        var builder = new StringBuilder(characters + line.Length);
        while (builder.Length < characters)
        {
            _ = builder.Append(line);
        }

        return builder.ToString(0, characters);
    }
}
