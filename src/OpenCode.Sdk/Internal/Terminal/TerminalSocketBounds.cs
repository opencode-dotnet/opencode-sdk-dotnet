namespace OpenCode.Sdk.Internal;

/// <summary>
/// The two bounds every <see cref="TerminalSocketCore{TFrame}"/> reads and closes under. They
/// live on a non-generic type deliberately: a static field on a generic type is one field per
/// closed construction (S2743), which would silently make a shared protocol bound per-family.
/// </summary>
internal static class TerminalSocketBounds
{
    /// <summary>The fixed total budget for one send, including serialization wait.</summary>
    public static readonly TimeSpan DefaultSendTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A message either family sends can be far larger than one receive. Taking it in fixed
    /// slices keeps the per-session buffer small whatever the message size is; the read loop
    /// assembles the fragments. <see cref="PtyFrameDecoder"/> records the normal family's replay
    /// chunking, which is the largest message measured against this bound.
    /// </summary>
    public const int ReceiveBufferSize = 16 * 1024;

    /// <summary>How long a graceful close may take before disposal stops waiting on the peer.</summary>
    public static readonly TimeSpan GracefulCloseTimeout = TimeSpan.FromSeconds(5);
}
