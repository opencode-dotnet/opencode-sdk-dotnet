namespace OpenCode.Sdk.Internal.Posix.Abstractions;

/// <summary>
/// The group signal seam, <c>kill(2)</c> addressed to a process group or to one process. The
/// launched server leads a session of its own, so its pid also names its process group, and a
/// signal to the group reaches the server together with every child it did not move into a group
/// of its own. A signal of zero only asks whether the target exists.
/// </summary>
internal interface IProcessGroupSignal
{
    /// <summary>Signals every member of a process group: <c>kill(-group, signal)</c>.</summary>
    /// <param name="processGroupId">The group's id, greater than one.</param>
    /// <param name="signal">The signal.</param>
    /// <returns>What the call achieved.</returns>
    public SignalDelivery SignalGroup(int processGroupId, ProcessSignal signal);

    /// <summary>Signals one process: <c>kill(pid, signal)</c>.</summary>
    /// <param name="processId">The process id, greater than one.</param>
    /// <param name="signal">The signal.</param>
    /// <returns>What the call achieved.</returns>
    public SignalDelivery SignalProcess(int processId, ProcessSignal signal);

    /// <summary>Asks whether any member of a process group exists: <c>kill(-group, 0)</c>.</summary>
    /// <param name="processGroupId">The group's id, greater than one.</param>
    /// <returns><see cref="SignalDelivery.NoSuchTarget"/> once the group is empty.</returns>
    public SignalDelivery ProbeGroup(int processGroupId);

    /// <summary>Asks whether a process exists: <c>kill(pid, 0)</c>. A zombie still exists.</summary>
    /// <param name="processId">The process id, greater than one.</param>
    /// <returns><see cref="SignalDelivery.NoSuchTarget"/> once no process has the id.</returns>
    public SignalDelivery ProbeProcess(int processId);
}
