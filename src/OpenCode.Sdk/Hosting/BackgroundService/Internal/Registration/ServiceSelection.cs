namespace OpenCode.Sdk.Internal;

/// <summary>
/// The validated, entry-time snapshot of a discovery or stop request. <see cref="Channel"/> is
/// null both for the shared release default and in direct-file mode, which has no channel to name;
/// the two are told apart by <see cref="DirectRegistrationFile"/>.
/// </summary>
/// <param name="Channel">The caller-named service channel, or null for the shared release registration.</param>
/// <param name="DirectRegistrationFile">The absolute registration path that bypasses channel resolution, or null.</param>
/// <param name="InstalledVersion">The migration comparand; in channel mode it defaults from <paramref name="ExpectedVersion"/>.</param>
/// <param name="ExpectedVersion">The exact version a discovered service's status must report, or null for any; a stop never filters.</param>
internal sealed record ServiceSelection(
    string? Channel,
    string? DirectRegistrationFile,
    string? InstalledVersion,
    string? ExpectedVersion)
{
    /// <summary>Validates the caller's discovery options and captures their values.</summary>
    /// <param name="options">The public options; null means every default.</param>
    /// <returns>The snapshot.</returns>
    /// <exception cref="ArgumentException">A value is blank, the direct file is relative, or the
    /// members contradict one another.</exception>
    public static ServiceSelection Snapshot(OpenCodeServerDiscoverOptions? options) =>
        options is null
            ? new ServiceSelection(null, null, null, null)
            : Validate(
                nameof(OpenCodeServerDiscoverOptions) + ".",
                nameof(options),
                options.Channel,
                options.RegistrationFilePath,
                options.InstalledVersion,
                options.ExpectedVersion);

    /// <summary>Validates the caller's stop options and captures their values.</summary>
    /// <param name="options">The public options; null means every default.</param>
    /// <returns>The snapshot; a stop carries no expected version.</returns>
    /// <exception cref="ArgumentException">A value is blank, the direct file is relative, or the
    /// members contradict one another.</exception>
    public static ServiceSelection Snapshot(OpenCodeServerStopOptions? options) =>
        options is null
            ? new ServiceSelection(null, null, null, null)
            : Validate(
                nameof(OpenCodeServerStopOptions) + ".",
                nameof(options),
                options.Channel,
                options.RegistrationFilePath,
                options.InstalledVersion,
                expectedVersion: null);

    /// <summary>Validates the caller's ensure options and captures their values.</summary>
    /// <param name="options">The public options; null means every default.</param>
    /// <returns>The snapshot.</returns>
    /// <exception cref="ArgumentException">A value is blank, the direct file is relative, or the
    /// members contradict one another.</exception>
    public static ServiceSelection Snapshot(OpenCodeServerEnsureOptions? options) =>
        options is null
            ? new ServiceSelection(null, null, null, null)
            : Validate(
                nameof(OpenCodeServerEnsureOptions) + ".",
                nameof(options),
                options.Channel,
                options.RegistrationFilePath,
                options.InstalledVersion,
                options.ExpectedVersion);

    /// <summary>
    /// The one rule set both option types share; <paramref name="prefix"/> names the type in every
    /// message and <paramref name="paramName"/> is the public method's parameter the exception names.
    /// </summary>
    private static ServiceSelection Validate(
        string prefix, string paramName, string? channel, string? registrationFilePath, string? installedVersion, string? expectedVersion)
    {
        if (IsBlank(channel))
        {
            throw new ArgumentException(prefix + "Channel cannot be blank; leave it null to use the shared release registration.", paramName);
        }

        if (IsBlank(registrationFilePath))
        {
            throw new ArgumentException(prefix + "RegistrationFilePath cannot be blank; leave it null to resolve the registration by channel.", paramName);
        }

        if (IsBlank(installedVersion))
        {
            throw new ArgumentException(prefix + "InstalledVersion cannot be blank; leave it null to migrate by channel prefix only.", paramName);
        }

        if (IsBlank(expectedVersion))
        {
            throw new ArgumentException(prefix + "ExpectedVersion cannot be blank; leave it null to accept any ready service.", paramName);
        }

        if (registrationFilePath is not null)
        {
            if (channel is not null)
            {
                throw new ArgumentException(prefix + "RegistrationFilePath and Channel cannot both be set; a direct registration file has no channel.", paramName);
            }

            if (!IsFullyQualified(registrationFilePath))
            {
                throw new ArgumentException(prefix + "RegistrationFilePath must be an absolute path.", paramName);
            }

            if (installedVersion is not null)
            {
                throw new ArgumentException(prefix + "InstalledVersion has no meaning with RegistrationFilePath; a direct registration file is never migrated.", paramName);
            }

            return new ServiceSelection(null, registrationFilePath, null, expectedVersion);
        }

        if (installedVersion is not null && expectedVersion is not null &&
            !string.Equals(installedVersion, expectedVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(prefix + "InstalledVersion and ExpectedVersion must be equal when both are set; the first-party CLI carries one compiled identity.", paramName);
        }

        return new ServiceSelection(channel, null, installedVersion ?? expectedVersion, expectedVersion);
    }

    private static bool IsBlank(string? value) => value is not null && string.IsNullOrWhiteSpace(value);

    /// <summary>
    /// Absolute means fully qualified, not merely rooted: on Windows <c>C:service.json</c> is
    /// drive-relative and <c>\service.json</c> is current-drive-relative, and both resolve against
    /// process state the caller never named, so both are refused rather than answered with null.
    /// </summary>
    private static bool IsFullyQualified(string path)
    {
#if NET
        return Path.IsPathFullyQualified(path);
#else
        // netstandard2.0 and net472 have no IsPathFullyQualified; this is the rule it applies.
        if (Path.DirectorySeparatorChar == '\\')
        {
            if (path.Length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1]))
            {
                // A UNC or device path.
                return true;
            }

            return path.Length >= 3 && IsAsciiLetter(path[0]) && path[1] == ':' && IsSeparator(path[2]);
        }

        return path.Length > 0 && path[0] == '/';
#endif
    }

#if !NET
    private static bool IsSeparator(char value) => value is '\\' or '/';

    private static bool IsAsciiLetter(char value) => value is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');
#endif
}
