using System.Diagnostics;
using System.Net.WebSockets;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// The one owner of the caller-cancel-versus-transport rule: an exception caught in a pipeline
/// phase is classified against the caller's token first, then the root client's lifetime. Other
/// faults become transport failures named by phase. Knowledge source: BCL-derived — the exception surfaces of <see cref="HttpClient"/>
/// sends, content reads, and task timeout machinery.
/// </summary>
internal static class FailureClassification
{
    /// <summary>
    /// The object a disposed root client names, <c>OpenCodeClient</c>'s full name. The transport spine
    /// compiles without the client type, so it carries the name rather than the type.
    /// </summary>
    private const string DisposedClientName = "OpenCode.Sdk.OpenCodeClient";

    /// <summary>Creates the failure that reports the disposed root client.</summary>
    public static ObjectDisposedException DisposedClient() => new(DisposedClientName);

    /// <summary>Owns exactly the faults the phase can produce; everything else stays the caller's.</summary>
    public static bool Handles(Exception exception, FailurePhase phase)
    {
        if (exception is OperationCanceledException)
        {
            return true;
        }

        return phase switch
        {
            // ObjectDisposedException covers a dispose-during-send race; the pre-send disposed
            // guard reports caller misuse before this map is reachable.
            FailurePhase.Send => exception is HttpRequestException or ObjectDisposedException,

            // InvalidOperationException carries the decoding policy's charset refusal;
            // TimeoutException is the body wait giving up on the transport budget.
            FailurePhase.ResponseBodyRead => exception
                is HttpRequestException or IOException or ObjectDisposedException
                or InvalidOperationException or TimeoutException,

            FailurePhase.EventStreamRead => exception is HttpRequestException or IOException or ObjectDisposedException,

            // A WebSocket fault arrives as WebSocketException; a torn-down socket as
            // ObjectDisposedException, and the underlying stream dying as IOException.
            FailurePhase.PtyWebSocketRead or FailurePhase.PtyWebSocketWrite => exception
                is WebSocketException or IOException or ObjectDisposedException,
            _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown failure phase."),
        };
    }

    /// <summary>Maps a handled exception onto the one the phase throws.</summary>
    public static Exception Map(Exception exception, FailurePhase phase, CancellationToken cancellationToken,
        CancellationToken lifetimeToken = default)
    {
        Debug.Assert(Handles(exception, phase), "Only exceptions the phase handles are classified.");

        if (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested && !lifetimeToken.IsCancellationRequested)
        {
            // Neither the caller nor the client asked for this cancellation: the transport timed out.
            return new OpenCodeTransportException(TimeoutMessage(phase), exception);
        }

        return MapFault(exception, phase, cancellationToken, lifetimeToken);
    }

    /// <summary>
    /// Classifies a fault against the caller and client lifetime: the shared tail of
    /// <see cref="Map"/>, and the whole mapping for a fault the phase's own interruption caused.
    /// Once the SDK has interrupted a phase itself — disposing the content under a cancelled
    /// progress window — whatever the BCL throws afterwards is a consequence of that
    /// interruption, not a new fault, and needs no place in <see cref="Handles"/>.
    /// </summary>
    public static Exception MapFault(Exception exception, FailurePhase phase, CancellationToken cancellationToken,
        CancellationToken lifetimeToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (cancellationToken.IsCancellationRequested)
        {
            return CallerCancellation(exception, CanceledMessage(phase), cancellationToken);
        }

        return lifetimeToken.IsCancellationRequested
            ? DisposedClient()
            : new OpenCodeTransportException(FaultMessage(phase), exception);
    }

    /// <summary>Retains the cause while reporting the cancellation identity of the caller at this boundary.</summary>
    public static OperationCanceledException CallerCancellation(Exception failure, string message, CancellationToken cancellationToken) =>
        failure is OperationCanceledException canceled && canceled.CancellationToken == cancellationToken
            ? canceled
            : new OperationCanceledException(message, failure, cancellationToken);

    private static string FaultMessage(FailurePhase phase) => phase switch
    {
        FailurePhase.Send => "The opencode server could not be reached.",
        FailurePhase.ResponseBodyRead => "The opencode response body could not be read.",
        FailurePhase.EventStreamRead => "The opencode event stream could not be read.",
        FailurePhase.PtyWebSocketRead => "The opencode PTY WebSocket could not be read.",
        FailurePhase.PtyWebSocketWrite => "The opencode PTY WebSocket could not be written to.",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown failure phase."),
    };

    private static string TimeoutMessage(FailurePhase phase) =>
        phase is FailurePhase.Send
            ? "The opencode server did not respond within the transport timeout."
            : FaultMessage(phase);

    private static string CanceledMessage(FailurePhase phase) => phase switch
    {
        FailurePhase.Send => "The opencode request was canceled.",
        FailurePhase.ResponseBodyRead => "The opencode response body read was canceled.",
        FailurePhase.EventStreamRead => "The opencode event stream read was canceled.",
        FailurePhase.PtyWebSocketRead => "The opencode PTY WebSocket read was canceled.",
        FailurePhase.PtyWebSocketWrite => "The opencode PTY WebSocket write was canceled.",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown failure phase."),
    };
}
