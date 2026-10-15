namespace OpenCode.Sdk.Internal;

/// <summary>
/// One running process, identified the way Aspire's <c>DcpProcessIdentity</c> and psutil identify
/// one: the pid together with the start time the operating system recorded for it. A pid alone is
/// reused once its process is gone; a pid whose start time changed belongs to a newer process, and
/// no signal meant for the old one may reach it.
/// </summary>
/// <param name="ProcessId">The pid.</param>
/// <param name="StartTimeUtc">The process's creation time as the platform reports it, in UTC.</param>
internal readonly record struct ProcessIdentity(int ProcessId, DateTime StartTimeUtc);
