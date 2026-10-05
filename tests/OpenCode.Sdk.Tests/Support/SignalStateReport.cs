namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// A server stand-in that reports the signal state it started with: the readiness line first, then
/// the report, from a process the shell became through <c>exec</c>, which keeps the ignored
/// dispositions and the mask it was given (a shell that forks the reporter and waits blocks signals
/// around the wait, and is no witness). Linux reads <c>/proc/self/status</c>; macOS has no such
/// file and no <c>ps</c> column for ignored signals, so Perl reports its own state there, and
/// Perl's <c>%SIG</c> shows an ignore it inherited as <c>IGNORE</c>.
/// </summary>
internal static class SignalStateReport
{
    /// <summary>The readiness line the stand-in prints before its report.</summary>
    public const string ReadyLine = "{\"url\":\"http://127.0.0.1:1\"}";

    /// <summary>The macOS report of a process at its default signal state.</summary>
    public const string MacDefaultState = "INT=DEFAULT PIPE=DEFAULT BLOCKED=";

    private const string LinuxReport = "grep -E '^Sig(Ign|Blk):' /proc/self/status";

    private const string MacReport =
        "perl -MPOSIX -e 'my $o = POSIX::SigSet->new; sigprocmask(SIG_BLOCK, POSIX::SigSet->new(), $o); "
        + "print \"INT=\", ($SIG{INT} // \"DEFAULT\"), \" PIPE=\", ($SIG{PIPE} // \"DEFAULT\"), \" BLOCKED=\", join(\",\", grep { $o->ismember($_) } 1..31), \"\\n\"'";

    /// <summary>The report command alone, for a shell to run as its last command.</summary>
    public static string Report => OperatingSystem.IsLinux() ? LinuxReport : MacReport;

    /// <summary>The stand-in: readiness on stdout, then the report on stdout.</summary>
    /// <returns>The command.</returns>
    public static IReadOnlyList<string> ToStandardOutput() =>
        ["/bin/sh", "-c", "printf '%s\\n' '" + ReadyLine + "'; exec " + Report];

    /// <summary>The stand-in: readiness on stdout, then the report into the file the variable names.</summary>
    /// <param name="variable">The environment variable that holds the report file's path.</param>
    /// <returns>The command.</returns>
    public static IReadOnlyList<string> ToFileIn(string variable) =>
        ["/bin/sh", "-c", "printf '%s\\n' '" + ReadyLine + "'; exec " + Report + " > \"$" + variable + "\""];
}
