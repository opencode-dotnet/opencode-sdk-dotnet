namespace OpenCode.Sdk;

/// <summary>
/// Shapes one <c>OpenCodeServer.EnsureAsync</c> call. Every member is optional; null means the
/// default, and a blank value is refused rather than treated as unset. The values are read once,
/// when the call starts.
/// </summary>
public sealed class OpenCodeServerEnsureOptions
{
    /// <summary>Gets the pinned client's default service command (<c>promise/service.ts</c> <c>ensure</c>), the one place it is spelled.</summary>
    internal static IReadOnlyList<string> DefaultCommand { get; } = ["opencode", "serve", "--service"];

    /// <summary>
    /// Gets or sets the service channel whose registration to ensure. Null reads the shared release
    /// registration (<c>service.json</c>) that the installed <c>opencode</c> command publishes; a
    /// named channel (<c>local</c>, <c>dev</c>, a custom name) follows the CLI's filename and
    /// legacy-migration rules for that channel. Cannot be combined with
    /// <see cref="RegistrationFilePath"/>.
    /// </summary>
    public string? Channel { get; set; }

    /// <summary>
    /// Gets or sets an absolute path to a registration file to ensure directly, bypassing channel
    /// resolution, the environment roots, and legacy migration. Cannot be combined with
    /// <see cref="Channel"/> or <see cref="InstalledVersion"/>.
    /// </summary>
    public string? RegistrationFilePath { get; set; }

    /// <summary>
    /// Gets or sets the CLI version whose legacy registration may be migrated to the current
    /// filename, the SDK's stand-in for the version compiled into the installed CLI. Only legacy
    /// migration reads it; the SDK never infers it. Defaults to <see cref="ExpectedVersion"/> in
    /// channel mode, and must equal it when both are set.
    /// </summary>
    public string? InstalledVersion { get; set; }

    /// <summary>
    /// Gets or sets the exact version a reused or replaced service must report; null accepts any
    /// ready compatible service. <see cref="VersionPolicy"/> decides whether this value is stripped,
    /// kept in the election loop, or used for the error preamble; <see cref="OpenCodeServerVersionPolicy.Replace"/>
    /// and <see cref="OpenCodeServerVersionPolicy.Error"/> refuse a null value.
    /// </summary>
    public string? ExpectedVersion { get; set; }

    /// <summary>
    /// Gets or sets how a version mismatch is handled. The default is
    /// <see cref="OpenCodeServerVersionPolicy.Ignore"/>.
    /// </summary>
    public OpenCodeServerVersionPolicy VersionPolicy { get; set; }

    /// <summary>
    /// Gets or sets the service command: the executable followed by its arguments. The default is
    /// <c>opencode serve --service</c>. The first entry is resolved the way
    /// <see cref="OpenCodeServer.StartAsync"/> resolves it, through the launcher's executable
    /// search, before a contender is spawned.
    /// </summary>
    public IReadOnlyList<string> Command { get; set; } = [.. DefaultCommand];

    /// <summary>
    /// Gets or sets extra environment entries layered onto a spawned contender after the channel's
    /// service-config map and before the handoff variable.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Environment { get; set; }

    /// <summary>
    /// Gets or sets a callback invoked at most once before a new service process is spawned, with
    /// the reason and, for a version mismatch, the previous version.
    /// </summary>
    public Action<OpenCodeServerEnsureReason, string?>? OnStart { get; set; }
}
