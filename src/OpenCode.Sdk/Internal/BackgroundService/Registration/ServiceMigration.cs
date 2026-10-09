using OpenCode.Sdk.Internal.BackgroundService.Abstractions;
using OpenCode.Sdk.Internal.Diagnostics;

namespace OpenCode.Sdk.Internal.BackgroundService.Registration;

/// <summary>
/// The pinned CLI's legacy copy (<c>migrateRegistration</c> and <c>migrateConfig</c> in
/// <c>service-config.ts</c>): a donor written under the earlier hashed filename is copied byte for
/// byte to the current name, exclusively, once, and only when its version belongs to the channel; a
/// config donor is copied only when it decodes under the current shape, so a legacy boolean
/// <c>remote</c> stays behind. The copy is a convenience the daemon's own registration supersedes,
/// so every write failure is swallowed the way upstream's <c>Effect.ignore</c> swallows it, no
/// directory is created, and no donor is ever removed.
/// </summary>
internal sealed class ServiceMigration(IServiceFileSystem fileSystem)
{
    /// <summary>Applies the registration and config migrations a selection calls for.</summary>
    /// <param name="selection">The validated selection; direct-file mode and the null channel migrate nothing.</param>
    /// <param name="paths">The resolved paths.</param>
    /// <param name="cancellationToken">The caller's token; cancellation is the one failure that propagates.</param>
    /// <returns>A task that completes when every applicable donor has been considered.</returns>
    public async Task ApplyAsync(ServiceSelection selection, ServicePaths paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(paths);

        if (selection.DirectRegistrationFile is not null || selection.Channel is null)
        {
            return;
        }

        foreach (var donor in paths.LegacyRegistrationFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = await fileSystem.TryReadAllBytesAsync(donor, cancellationToken).ConfigureAwait(false);
            if (bytes is null)
            {
                continue;
            }

            var registration = ServiceRegistrationReader.TryRead(bytes);
            if (registration is null ||
                !VersionBelongsToChannel(registration.Version, selection.Channel, selection.InstalledVersion))
            {
                continue;
            }

            await CopyAsync(paths.RegistrationFile, bytes, cancellationToken).ConfigureAwait(false);
        }

        if (paths.LegacyConfigFile is { } legacyConfig && paths.ConfigFile is { } configFile)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = await fileSystem.TryReadAllBytesAsync(legacyConfig, cancellationToken).ConfigureAwait(false);
            if (bytes is not null && ServiceConfigReader.IsCurrent(bytes))
            {
                await CopyAsync(configFile, bytes, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// <c>versionBelongsToChannel</c> at the pin: the exact installed version when the SDK was
    /// given one, otherwise the pre-release spelling <c>0.0.0-&lt;channel&gt;-N</c> or
    /// <c>0.0.0-&lt;channel&gt;-N.M</c> with decimal segments, over the raw channel string. A stable
    /// release version never matches the prefix, so a release registration is claimed only by an
    /// exact comparand.
    /// </summary>
    /// <param name="version">The donor's version, or null.</param>
    /// <param name="channel">The raw channel.</param>
    /// <param name="installedVersion">The exact comparand, or null when the SDK has none.</param>
    /// <returns>True when the donor may be copied.</returns>
    internal static bool VersionBelongsToChannel(string? version, string channel, string? installedVersion)
    {
        if (version is null)
        {
            return false;
        }

        if (installedVersion is not null && string.Equals(version, installedVersion, StringComparison.Ordinal))
        {
            return true;
        }

        var prefix = "0.0.0-" + channel + "-";
        if (!version.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        return IsDecimalSegments(version.AsSpan(prefix.Length));
    }

    /// <summary><c>^\d+(?:\.\d+)?$</c> without a Regex: one run of digits, optionally one dot and a second run.</summary>
    private static bool IsDecimalSegments(ReadOnlySpan<char> rest)
    {
        var dot = rest.IndexOf('.');
        if (dot < 0)
        {
            return IsDigits(rest);
        }

        return IsDigits(rest[..dot]) && IsDigits(rest[(dot + 1)..]);
    }

    private static bool IsDigits(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    [SlopwatchSuppress(
        "SW003",
        "The pinned CLI's migration copy runs under Effect.ignore (service-config.ts): a missing directory, a locked or read-only target, a full disk, or an arm that cannot create the copy safely never fails a lookup.")]
    private async Task CopyAsync(string target, byte[] bytes, CancellationToken cancellationToken)
    {
        try
        {
            _ = await fileSystem.TryCreateExclusiveAsync(target, bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Effect.ignore: an existing target is kept either way, and a failed copy is no registration.
        }
    }
}
