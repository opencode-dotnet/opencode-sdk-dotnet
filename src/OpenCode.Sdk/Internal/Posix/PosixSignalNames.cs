using System.Collections.Frozen;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// The standard signal names of one operating system, by number, so an exit reports the name
/// upstream reports (Node's <c>signal</c> argument, such as <c>SIGTERM</c>). Linux and macOS
/// number several signals differently, so each has its own table, taken from its
/// <c>signal.h</c>. A number neither table names (a Linux real-time signal) has no name, and none
/// is made up for it.
/// </summary>
internal sealed class PosixSignalNames
{
    private readonly FrozenDictionary<int, string> _names;

    private PosixSignalNames(IEnumerable<string> namesFromOne) =>
        _names = namesFromOne
            .Select(static (name, index) => new KeyValuePair<int, string>(index + 1, name))
            .ToFrozenDictionary();

    /// <summary>Gets Linux's names, signals 1 to 31 (glibc and musl agree on them).</summary>
    public static PosixSignalNames Linux { get; } = new(
    [
        "SIGHUP", "SIGINT", "SIGQUIT", "SIGILL", "SIGTRAP", "SIGABRT", "SIGBUS", "SIGFPE",
        "SIGKILL", "SIGUSR1", "SIGSEGV", "SIGUSR2", "SIGPIPE", "SIGALRM", "SIGTERM", "SIGSTKFLT",
        "SIGCHLD", "SIGCONT", "SIGSTOP", "SIGTSTP", "SIGTTIN", "SIGTTOU", "SIGURG", "SIGXCPU",
        "SIGXFSZ", "SIGVTALRM", "SIGPROF", "SIGWINCH", "SIGIO", "SIGPWR", "SIGSYS",
    ]);

    /// <summary>Gets macOS's names, signals 1 to 31.</summary>
    public static PosixSignalNames MacOS { get; } = new(
    [
        "SIGHUP", "SIGINT", "SIGQUIT", "SIGILL", "SIGTRAP", "SIGABRT", "SIGEMT", "SIGFPE",
        "SIGKILL", "SIGBUS", "SIGSEGV", "SIGSYS", "SIGPIPE", "SIGALRM", "SIGTERM", "SIGURG",
        "SIGSTOP", "SIGTSTP", "SIGCONT", "SIGCHLD", "SIGTTIN", "SIGTTOU", "SIGIO", "SIGXCPU",
        "SIGXFSZ", "SIGVTALRM", "SIGPROF", "SIGWINCH", "SIGINFO", "SIGUSR1", "SIGUSR2",
    ]);

    /// <summary>Gets the table of the host's operating system; on Windows, which has no signals, Linux's, which nothing asks.</summary>
    public static PosixSignalNames ForCurrentProcess => OperatingSystem.IsMacOS() ? MacOS : Linux;

    /// <summary>Names a signal number.</summary>
    /// <param name="number">The signal number.</param>
    /// <returns>The standard name, or <see langword="null"/> when the table names none.</returns>
    public string? NameOf(int number) => _names.TryGetValue(number, out var name) ? name : null;
}
