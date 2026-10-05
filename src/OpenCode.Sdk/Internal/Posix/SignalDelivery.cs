namespace OpenCode.Sdk.Internal.Posix;

/// <summary>What one <c>kill(2)</c> call achieved.</summary>
internal enum SignalDelivery
{
    /// <summary>The signal reached at least one process; for signal zero, the target exists.</summary>
    Delivered,

    /// <summary><c>ESRCH</c>: no such process, or no member of the group, exists.</summary>
    NoSuchTarget,

    /// <summary>Any other failure, <c>EPERM</c> among them: the target may exist and was not signalled.</summary>
    Refused,
}
