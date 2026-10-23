namespace OpenCode.Sdk.Tests.Harness;

/// <summary>
/// A loopback port that stays bound until disposal, so the OS cannot hand it to another
/// reservation meanwhile; <see cref="LoopbackPortReservation.Hold"/> makes one.
/// </summary>
/// <param name="port">The bound port.</param>
/// <param name="release">Unbinds the port.</param>
internal sealed class HeldLoopbackPort(int port, Action release) : IDisposable
{
    /// <summary>Gets the bound port.</summary>
    public int Port { get; } = port;

    /// <inheritdoc />
    public void Dispose() => release();
}
