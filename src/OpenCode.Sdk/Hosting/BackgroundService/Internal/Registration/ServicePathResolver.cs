namespace OpenCode.Sdk.Internal;

/// <summary>
/// Reproduces the pinned CLI's channel, XDG-root, and legacy-donor rules
/// (<c>packages/cli/src/services/service-config.ts</c>, <c>packages/util/src/global-roots.ts</c>,
/// <c>packages/util/src/global.ts</c>): a pure policy over a supplied environment.
/// </summary>
internal sealed class ServicePathResolver(IServiceEnvironment environment)
{
    private const string App = "opencode";
    private const string SharedFileName = "service.json";
    private const string LocalChannel = "local";

    /// <summary>Resolves the files a selection reads.</summary>
    /// <param name="selection">The validated selection.</param>
    /// <returns>The paths.</returns>
    /// <exception cref="OpenCodeServerException">A fallback needed the user home and none resolved.</exception>
    public ServicePaths Resolve(ServiceSelection selection)
    {
        if (selection.DirectRegistrationFile is { } directFile)
        {
            return new ServicePaths(directFile, ConfigFile: null, LegacyRegistrationFiles: [], LegacyConfigFile: null);
        }

        var stateDirectory = Path.Combine(Root("XDG_STATE_HOME", ".local", "state"), App);
        var configDirectory = ConfigDirectory();

        if (selection.Channel is null)
        {
            // The shared release registration, read the way @opencode/client's fallback() reads
            // it: no legacy name exists for the release channel, so nothing migrates.
            return new ServicePaths(
                Path.Combine(stateDirectory, SharedFileName),
                Path.Combine(configDirectory, SharedFileName),
                LegacyRegistrationFiles: [],
                LegacyConfigFile: null);
        }

        var channel = selection.Channel;
        var fileName = FileName(channel);
        var legacyName = LegacyFileName(channel);
        var donors = new List<string>(capacity: 2);
        if (legacyName is not null)
        {
            donors.Add(Path.Combine(stateDirectory, legacyName));
        }

        if (!string.Equals(fileName, SharedFileName, StringComparison.Ordinal) &&
            !string.Equals(channel, LocalChannel, StringComparison.Ordinal))
        {
            donors.Add(Path.Combine(stateDirectory, SharedFileName));
        }

        return new ServicePaths(
            Path.Combine(stateDirectory, fileName),
            Path.Combine(configDirectory, fileName),
            donors,
            legacyName is null ? null : Path.Combine(configDirectory, legacyName));
    }

    /// <summary>The current registration filename: the release channels share one file.</summary>
    /// <param name="channel">The raw channel.</param>
    /// <returns>The filename.</returns>
    internal static string FileName(string channel) =>
        IsReleaseChannel(channel) ? SharedFileName : "service-" + Sanitize(channel) + ".json";

    /// <summary>The hashed legacy filename, or null for the two channels that never had one.</summary>
    /// <param name="channel">The raw channel.</param>
    /// <returns>The legacy filename, or null.</returns>
    internal static string? LegacyFileName(string channel) =>
        channel is "latest" or LocalChannel ? null : ServiceLegacyFilename.For(channel);

    private static bool IsReleaseChannel(string channel) =>
        channel is "latest" or "dev" or "beta" or "next";

    private static string Sanitize(string channel)
    {
        // The CLI's replace(/[^a-zA-Z0-9._-]/g, "-") runs over UTF-16 code units (no `u` flag),
        // so a surrogate pair becomes two dashes; iterating char reproduces that exactly.
        var chars = channel.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            var keep = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-';
            if (!keep)
            {
                chars[i] = '-';
            }
        }

        return new string(chars);
    }

    private string ConfigDirectory()
    {
        // The variable replaces the whole config root: no "opencode" segment beneath it
        // (packages/util/src/global.ts, acquire({ config: process.env.OPENCODE_CONFIG_DIR ?? Path.config })).
        // The pattern (rather than string.IsNullOrEmpty) narrows to non-null on every target:
        // net472's BCL carries no NotNullWhen on IsNullOrEmpty.
        if (environment.GetEnvironmentVariable("OPENCODE_CONFIG_DIR") is { Length: > 0 } overrideDirectory)
        {
            return overrideDirectory;
        }

        return Path.Combine(Root("XDG_CONFIG_HOME", ".config"), App);
    }

    private string Root(string variable, params string[] homeRelativeFallback)
    {
        if (environment.GetEnvironmentVariable(variable) is { Length: > 0 } value)
        {
            return value;
        }

        if (environment.UserProfile is not { Length: > 0 } home)
        {
            throw new OpenCodeServerException(
                $"{variable} is not set and no user home directory resolves (HOME, USERPROFILE, and the profile folder are all empty), so the background-service roots cannot be located.");
        }

        return Path.Combine([home, .. homeRelativeFallback]);
    }
}
