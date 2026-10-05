namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// What one open descriptor holds, as the operating system names it. A pipe end is named on Linux
/// by the pipe's inode, which both ends share, and on macOS by this end's address and its peer's.
/// Anything else is named by what <c>/proc</c> or <c>lsof</c> reports for it, which is the same for
/// two descriptors that share one open file.
/// </summary>
/// <param name="Target">The name: <c>pipe:[inode]</c> or a path on Linux; a pipe end's address, or the type, device and name, on macOS.</param>
/// <param name="Peer">The other end's address, for a pipe end on macOS; null otherwise.</param>
/// <param name="IsPipe">Whether the descriptor holds a pipe end.</param>
internal sealed record OpenDescriptor(string Target, string? Peer, bool IsPipe)
{
    /// <summary>Reports whether two descriptors hold ends of the same pipe.</summary>
    /// <param name="other">The other descriptor.</param>
    /// <returns>True when both hold a pipe end, and they are the same end or the two ends of one pipe.</returns>
    public bool SharesPipeWith(OpenDescriptor other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return IsPipe && other.IsPipe &&
               (string.Equals(Target, other.Target, StringComparison.Ordinal) ||
                (Peer is not null && string.Equals(Peer, other.Target, StringComparison.Ordinal)) ||
                (other.Peer is not null && string.Equals(other.Peer, Target, StringComparison.Ordinal)));
    }

    /// <inheritdoc />
    public override string ToString() => Peer is null ? Target : Target + " -> " + Peer;
}
