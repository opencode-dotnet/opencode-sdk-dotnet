namespace OpenCode.Sdk;

/// <summary>
/// Configures how <see cref="OpenCodeServer.StartAsync"/> launches a standalone server. The
/// class stays settable for the options pattern; the start snapshots every member, so later
/// mutation never reaches a started server.
/// </summary>
public sealed class OpenCodeServerOptions
{
    /// <summary>
    /// Gets or sets the server command: the executable followed by its leading arguments. The
    /// launcher appends <c>--stdio --port 0</c> — the reference client's exact standalone argv.
    /// The default runs <c>opencode serve</c> from the PATH: the <c>@opencode/cli</c>
    /// package installs <c>opencode</c> (plus a transitional <c>opencode2</c> alias)
    /// pointing at one executable. Upstream's
    /// own standalone mode re-invokes the executable it is already running rather than naming
    /// one, which an SDK cannot do. Tests and tools point this at a source run instead.
    /// <para>
    /// The first entry is resolved the way a shell resolves it, before anything is spawned: a
    /// rooted path or one carrying a directory separator is used as written, and a bare name is
    /// searched through the PATH entries in order — on Windows with each PATHEXT extension
    /// appended when the name carries none, so an npm <c>.cmd</c> shim is found and started
    /// through cmd.exe. A leading argument containing a cmd metacharacter is refused for such a
    /// shim rather than escaped.
    /// </para>
    /// <para>
    /// On Windows the started process is placed in a job that ends it when its owner ends, however
    /// the owner ends. A batch shim's own child, the real server, is outside that job: it ends with a
    /// crashed owner only through its stdin lease. The default <c>opencode</c> of an npm install is
    /// such a shim, so a host that needs the full guarantee names the path of <c>opencode.exe</c>
    /// itself.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Command { get; set; } = ["opencode", "serve"];

    /// <summary>
    /// Gets or sets the child's working directory; null inherits the caller's, and a blank value
    /// counts as null. On Linux and macOS a relative command path is resolved against the caller's
    /// directory, not this one. Where the C library cannot change a spawned child's directory
    /// itself (glibc before 2.29), the child is started through <c>/usr/bin/env -C</c>, which keeps
    /// the same process; a directory that does not exist then fails the start as an exit with code
    /// 125, naming the directory in the stderr tail.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Gets or sets extra environment entries for the child. The launcher writes its own
    /// generated <c>OPENCODE_PASSWORD</c> entry after these, so a supplied value can never
    /// shadow the lease credential.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Environment { get; set; }

    /// <summary>
    /// Gets or sets how long the start waits for the JSON readiness line before failing and
    /// ending the child; must be positive. The default is 60 seconds — source-run servers boot
    /// slowly on cold CI runners.
    /// </summary>
    public TimeSpan ReadinessTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets or sets the grace before the forced end; zero means immediate escalation. On Windows it
    /// starts with the first tree kill (<c>taskkill /T /F</c>), which it bounds, and runs to the
    /// second, which runs only when the server is still alive. On Linux and macOS it runs
    /// from <c>SIGTERM</c> to the server's process group to <c>SIGKILL</c> to the group: within it
    /// the server has to exit, and then every other member of its group. A failed start uses the
    /// same grace. The default mirrors the reference client's 3-second force-kill window.
    /// </summary>
    public TimeSpan GracefulShutdownTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Gets or sets an optional caller-created collector that retains a bounded tail of the
    /// child's stdout and stderr for pull snapshots. A collector binds to exactly one start
    /// attempt; supplying one an earlier start already bound is refused before anything is
    /// spawned. Null keeps the default: output is drained and, apart from the startup failure
    /// diagnostics, not retained.
    /// </summary>
    public OpenCodeServerOutput? Output { get; set; }
}
