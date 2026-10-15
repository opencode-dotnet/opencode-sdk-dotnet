using System.Net.WebSockets;
using System.Text;
using BenchmarkDotNet.Attributes;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Performance.Tests.Support;

namespace OpenCode.Sdk.Performance.Tests.Benchmarks;

/// <summary>
/// The persistent-PTY WebSocket read path in isolation — frame decode, replay/cursor handling, and
/// the delivered frame — with no live server and no real socket: a canned <see cref="ITerminalWebSocket"/>
/// replays recorded messages, the same way <see cref="PtySessionReadBenchmarks"/> replays the
/// normal family's frames. The complete rung drives <see cref="PersistentPtySession.ReadAsync"/>
/// end to end (receive, cross-receive reassembly above the shared 16 KiB
/// <see cref="TerminalSocketBounds.ReceiveBufferSize"/>, decode, yield); the decode-alone rung
/// isolates <see cref="PersistentPtyFrameDecoder.Decode"/> from the receive loop entirely, over the
/// exact assembled messages the complete rung decodes. This family's hierarchy differs from the
/// normal family's in framing, not shape: control frames (<c>attached</c>, and any type this SDK
/// does not know) ride text/JSON messages, while output rides binary messages carrying raw
/// terminal bytes verbatim. One case covers each control kind at the single-frame scale the
/// normal family's cursor case measures; the output cases reuse its exact small/large scale so a
/// number is comparable across families without re-deriving what "small" and "large" mean. The
/// receive-only and suspending rungs mirror <see cref="PtySessionReadBenchmarks"/>'s.
/// </summary>
[MemoryDiagnoser]
public class PersistentPtySessionReadBenchmarks : IAsyncDisposable
{
    private const string AttachedFixtureName = "attached-x1";
    private const string UnknownFixtureName = "unknown-x1";
    private const int SmallFrameCount = 1024;
    private const int LargeFrameCount = 64;
    private const int LargeFramePayloadBytes = 40 * 1024;
    private const string SeedAttachmentId = "att_bench";
    private const string SeedUnknownType = "scrollback_trimmed";

    private const string SeedInfoJson =
        "{\"id\":\"pty_bench\",\"title\":\"bench\",\"command\":\"/bin/bash\",\"args\":[],\"cwd\":\"/\",\"status\":\"running\","
        + "\"pid\":1,\"sessionID\":\"ses_bench\",\"foregroundProcess\":null,\"size\":{\"cols\":80,\"rows\":24},"
        + "\"output\":{\"head\":0,\"tail\":0}}";

    private const string AttachedFrameJson =
        "{\"type\":\"attached\",\"attachmentID\":\"" + SeedAttachmentId + "\",\"inputProtocol\":1,\"info\":" + SeedInfoJson
        + ",\"role\":\"controller\",\"generation\":1,\"replay\":{\"requestedOffset\":0,\"availableOffset\":0,\"endOffset\":0,\"truncated\":false}}";

    private const string UnknownFrameJson = "{\"type\":\"" + SeedUnknownType + "\",\"bytes\":1024}";

    private static readonly IReadOnlyList<(WireFixture Fixture, (WebSocketMessageType Type, byte[] Payload)[] Messages)> Cases =
    [
        AttachedCase(),
        UnknownCase(),
        OutputCase("output-small-x1024", SmallOutputPayload(), SmallFrameCount),
        OutputCase("output-large-x64", LargeOutputPayload(), LargeFrameCount),
    ];

    private (WebSocketMessageType Type, byte[] Payload)[] _messages = [];
    private CannedTerminalWebSocket? _socket;
    private PersistentPtySession? _session;
    private PersistentPtyAttachment? _attachment;
    private PersistentPtyFrame? _anyFrame;

    public static IEnumerable<WireFixture> Fixtures() => Cases.Select(static entry => entry.Fixture);

    [ParamsSource(nameof(Fixtures))]
    public WireFixture Fixture { get; set; } = null!;

    /// <summary>
    /// Validates the fixture once. Each measured invocation owns a fresh connection because
    /// terminal completion is permanent; construction and disposal are included. The attachment is seeded from
    /// a decoded <c>attached</c> frame rather than hand-built, so the fixture never drifts from
    /// what the decoder itself considers a valid grant.
    /// </summary>
    [GlobalSetup]
    public async Task SetupAsync()
    {
        _messages = MessagesFor(Fixture);
        _socket = new CannedTerminalWebSocket(_messages);
        _attachment = SeedAttachment();

        // Ownership stays local until the session has taken the core over — the same
        // unconditional-dispose-of-a-transferable-local shape PersistentPtySession.AttachAsync
        // uses, which is what satisfies CA2000 here without a redundant second disposal.
        TerminalSocketCore<PersistentPtyFrame>? core = null;
        try
        {
            core = NewCore(_socket);
            _session = new PersistentPtySession(core, _attachment);
            core = null;
        }
        finally
        {
            await (core?.DisposeAsync().AsTask() ?? Task.CompletedTask).ConfigureAwait(false);
        }

        var delivered = await CollectFramesAsync(_session).ConfigureAwait(false);
        if (delivered.Count != Fixture.Items)
        {
            throw new InvalidOperationException($"Fixture '{Fixture.Name}' did not deliver the expected frame count.");
        }

        _anyFrame = delivered[0];
        if (await ReceiveFramesAsync().ConfigureAwait(false) != Fixture.Items)
        {
            throw new InvalidOperationException($"Fixture '{Fixture.Name}' did not receive-only into the expected frame count.");
        }

        using (var suspending = new CannedTerminalWebSocket(_messages, suspend: true))
        {
            await using var suspendingCore = NewCore(suspending);
            await using var session = new PersistentPtySession(suspendingCore, _attachment);
            if (await CountFramesAsync(session).ConfigureAwait(false) != Fixture.Items || suspending.SuspendedReceives < Fixture.Items)
            {
                throw new InvalidOperationException($"Fixture '{Fixture.Name}' did not deliver its frames over suspended receives.");
            }
        }

        if (string.Equals(Fixture.Name, AttachedFixtureName, StringComparison.Ordinal))
        {
            if (delivered is not [PersistentPtyAttachedFrame { Attachment.AttachmentId: SeedAttachmentId }])
            {
                throw new InvalidOperationException("The attached fixture did not decode the expected attached frame.");
            }
        }
        else if (string.Equals(Fixture.Name, UnknownFixtureName, StringComparison.Ordinal))
        {
            if (delivered is not [PersistentPtyUnknownFrame { Type: SeedUnknownType }])
            {
                throw new InvalidOperationException("The unknown-type fixture did not decode the expected unknown-type carrier.");
            }
        }
        else if (delivered.Any(static frame => frame is not PersistentPtyOutputFrame { Data.Length: > 0 }))
        {
            throw new InvalidOperationException($"Fixture '{Fixture.Name}' did not decode into complete, non-empty output frames.");
        }

        if (DecodeFrames() != Fixture.Items)
        {
            throw new InvalidOperationException($"Fixture '{Fixture.Name}' did not decode-alone into the expected frame count.");
        }
    }

    /// <summary>The connection lifetime: construction, receive, reassembly, decode, delivery, and disposal.</summary>
    [Benchmark]
    public async Task<int> ReadFramesAsync()
    {
        using var socket = new CannedTerminalWebSocket(_messages);
        await using var core = NewCore(socket);
        await using var session = new PersistentPtySession(core, _attachment!);
        return await CountFramesAsync(session).ConfigureAwait(false);
    }

    /// <summary>The complete rung over receives that each suspend, as a live socket's do.</summary>
    [Benchmark]
    public async Task<int> ReadFramesSuspendingAsync()
    {
        using var socket = new CannedTerminalWebSocket(_messages, suspend: true);
        await using var core = NewCore(socket);
        await using var session = new PersistentPtySession(core, _attachment!);
        return await CountFramesAsync(session).ConfigureAwait(false);
    }

    /// <summary>The connection lifetime with a decoder that reads nothing: receive, reassembly, queue, delivery, disposal.</summary>
    [Benchmark]
    public async Task<int> ReceiveFramesAsync()
    {
        using var socket = new CannedTerminalWebSocket(_messages);
        await using var core = new TerminalSocketCore<PersistentPtyFrame>(
            socket, new NoOpFrameDecoder<PersistentPtyFrame>(_anyFrame!), PersistentPtyClosePolicy.Instance, typeof(PersistentPtySession));
        var frames = 0;
        await foreach (var frame in core.ReadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            _ = frame;
            frames++;
        }

        return frames;
    }

    /// <summary>Decode alone, over the exact messages the complete rung assembles: no socket, no receive loop.</summary>
    [Benchmark]
    public int DecodeFrames()
    {
        var frames = 0;
        foreach (var (type, payload) in _messages)
        {
            _ = PersistentPtyFrameDecoder.Instance.Decode(type, payload, payload.Length);
            frames++;
        }

        return frames;
    }

    [GlobalCleanup]
    public ValueTask CleanupAsync() => DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
        }

        _socket?.Dispose();
        GC.SuppressFinalize(this);
    }

    private static async Task<int> CountFramesAsync(PersistentPtySession session)
    {
        var frames = 0;
        await foreach (var frame in session.ReadAsync().ConfigureAwait(false))
        {
            _ = frame;
            frames++;
        }

        return frames;
    }

    private static async Task<List<PersistentPtyFrame>> CollectFramesAsync(PersistentPtySession session)
    {
        var frames = new List<PersistentPtyFrame>();
        await foreach (var frame in session.ReadAsync().ConfigureAwait(false))
        {
            frames.Add(frame);
        }

        return frames;
    }

    private static (WebSocketMessageType Type, byte[] Payload)[] MessagesFor(WireFixture fixture) =>
        Cases.First(entry => string.Equals(entry.Fixture.Name, fixture.Name, StringComparison.Ordinal)).Messages;

    private static TerminalSocketCore<PersistentPtyFrame> NewCore(ITerminalWebSocket socket) =>
        new(socket, PersistentPtyFrameDecoder.Instance, PersistentPtyClosePolicy.Instance, typeof(PersistentPtySession));

    /// <summary>
    /// Decodes the same <c>attached</c> wire bytes the <see cref="AttachedFixtureName"/> case
    /// replays into the grant a session's constructor requires, so the seed can never diverge from
    /// what that case measures.
    /// </summary>
    private static PersistentPtyAttachment SeedAttachment()
    {
        var payload = Encoding.UTF8.GetBytes(AttachedFrameJson);
        return ((PersistentPtyAttachedFrame)PersistentPtyFrameDecoder.Instance.Decode(WebSocketMessageType.Text, payload, payload.Length))
            .Attachment;
    }

    /// <summary>The single <c>attached</c> control frame, the size class the normal family's cursor case measures.</summary>
    private static (WireFixture Fixture, (WebSocketMessageType Type, byte[] Payload)[] Messages) AttachedCase()
    {
        var payload = Encoding.UTF8.GetBytes(AttachedFrameJson);
        var fixture = new WireFixture(AttachedFixtureName, payload, items: 1, payloadBytesPerItem: payload.Length);
        return (fixture, [(WebSocketMessageType.Text, payload)]);
    }

    /// <summary>The unknown-type carrier: a control frame whose type this SDK does not know, carried rather than refused.</summary>
    private static (WireFixture Fixture, (WebSocketMessageType Type, byte[] Payload)[] Messages) UnknownCase()
    {
        var payload = Encoding.UTF8.GetBytes(UnknownFrameJson);
        var fixture = new WireFixture(UnknownFixtureName, payload, items: 1, payloadBytesPerItem: payload.Length);
        return (fixture, [(WebSocketMessageType.Text, payload)]);
    }

    private static (WireFixture Fixture, (WebSocketMessageType Type, byte[] Payload)[] Messages) OutputCase(string name, byte[] payload, int count)
    {
        var messages = new (WebSocketMessageType, byte[])[count];
        for (var index = 0; index < count; index++)
        {
            messages[index] = (WebSocketMessageType.Binary, payload);
        }

        var fixture = new WireFixture(name, Repeat(payload, count), count, payloadBytesPerItem: payload.Length);
        return (fixture, messages);
    }

    /// <summary>A small realistic terminal line; output rides binary frames in this family, unlike the normal family's text frames.</summary>
    private static byte[] SmallOutputPayload() => "$ echo hello\r\nhello\r\n"u8.ToArray();

    /// <summary>
    /// A large output chunk sized to exceed the shared 16 KiB
    /// <see cref="TerminalSocketBounds.ReceiveBufferSize"/>, so replaying it forces exactly the
    /// cross-receive reassembly a real large replay chunk would.
    /// </summary>
    private static byte[] LargeOutputPayload()
    {
        const string line = "the quick brown fox jumps over the lazy dog\r\n";
        var builder = new StringBuilder(LargeFramePayloadBytes + line.Length);
        while (builder.Length < LargeFramePayloadBytes)
        {
            _ = builder.Append(line);
        }

        return Encoding.UTF8.GetBytes(builder.ToString(0, LargeFramePayloadBytes));
    }

    /// <summary>Concatenates one payload repeated <paramref name="count"/> times, for the fixture's reported wire bytes.</summary>
    private static byte[] Repeat(byte[] payload, int count)
    {
        var body = new byte[payload.Length * count];
        for (var index = 0; index < count; index++)
        {
            Array.Copy(payload, 0, body, index * payload.Length, payload.Length);
        }

        return body;
    }
}
