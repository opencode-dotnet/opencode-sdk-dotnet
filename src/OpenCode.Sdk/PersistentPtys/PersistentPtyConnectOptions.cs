using System.Diagnostics.CodeAnalysis;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk;

/// <summary>
/// Shapes one persistent PTY WebSocket connection and its local send budget. The send timeout
/// stays in the SDK; the other options ride the upgrade query. The SDK always negotiates framed input.
/// </summary>
[Experimental(ExperimentalDiagnostics.PersistentPty, UrlFormat = ExperimentalDiagnostics.PersistentPtyUrl)]
public sealed record PersistentPtyConnectOptions
{
    private readonly TerminalSendTimeout _sendTimeout = TerminalSendTimeout.Default;

    /// <summary>
    /// Gets the fixed total budget for one WebSocket input or resize send, including serialization wait.
    /// The default is 30 seconds; this does not limit connection establishment or command execution.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The budget is below one millisecond or above int.MaxValue milliseconds.</exception>
    public TimeSpan SendTimeout
    {
        get => _sendTimeout.Value;
        init => _sendTimeout = new TerminalSendTimeout(value);
    }

    /// <summary>
    /// The largest cursor the server accepts: it validates against the JavaScript safe-integer
    /// range and answers HTTP 400 before the upgrade for anything outside it. The SDK refuses the
    /// value here rather than spending a round trip on it.
    /// </summary>
    private const long MaximumCursor = 9_007_199_254_740_991;

    /// <summary>
    /// The smallest cursor the server accepts. Unlike the normal PTY family there is no live-only
    /// mode: zero means "replay from the oldest retained byte", not "replay nothing".
    /// </summary>
    private const long MinimumCursor = 0;

    /// <summary>Spelled out rather than interpolated: the bounds are fixed and culture-free.</summary>
    private const string CursorRangeFailure =
        "The persistent PTY cursor must be null to replay from the oldest retained byte, or between 0 and 9007199254740991.";

    private readonly long? _cursor;

    /// <summary>
    /// Gets the output cursor the replay resumes from; null replays from the oldest retained
    /// byte. A cursor pointing at trimmed output is advanced by the server, which reports the
    /// gap through <see cref="PersistentPtyReplayBounds.Truncated"/>. The anchor for a resume is
    /// the previous session's <see cref="PersistentPtyReplayCompleteFrame.EndOffset"/> or the
    /// terminal's <c>Info.Output.Tail</c>.
    /// </summary>
    public long? Cursor
    {
        get => _cursor;
        init
        {
            if (value is { } cursor && cursor is < MinimumCursor or > MaximumCursor)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, CursorRangeFailure);
            }

            _cursor = value;
        }
    }

    /// <summary>
    /// Gets the role the connection asks for; the server grants the role and reports what it
    /// granted through <see cref="PersistentPtyAttachment.Role"/>.
    /// </summary>
    public PersistentPtyRole Role { get; init; }

    /// <summary>
    /// Gets the identity to attach under; the server mints a random one when this is null.
    /// Reusing a previous attachment's identity is how a reconnect reclaims its own control.
    /// </summary>
    public string? AttachmentId { get; init; }

    /// <summary>
    /// Gets whether this connection takes control from the terminal's current controller. Without
    /// it a second controller is attached as an observer instead.
    /// </summary>
    public bool Takeover { get; init; }
}
