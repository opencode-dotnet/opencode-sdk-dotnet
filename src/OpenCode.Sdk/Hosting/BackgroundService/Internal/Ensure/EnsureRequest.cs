namespace OpenCode.Sdk.Internal;

/// <summary>
/// One <c>EnsureAsync</c> call's options, validated and copied when the call starts, before the
/// registration is read or anything is spawned, so a caller that reuses or changes its options
/// object mid-call changes nothing. The
/// version policy is resolved here the way the CLI resolves its <c>mismatch</c> flag
/// (<c>resolveManaged</c> in <c>server-connection.ts</c>): Ignore strips the expected version before the election,
/// Replace keeps it, Error keeps it after its two Discover calls.
/// </summary>
/// <param name="Selection">The registration the call targets.</param>
/// <param name="Command">The service command, executable first.</param>
/// <param name="Environment">The caller's overlay, copied, or null.</param>
/// <param name="OnStart">The at-most-once spawn callback, or null.</param>
/// <param name="Policy">The mismatch policy.</param>
internal sealed record EnsureRequest(
    ServiceSelection Selection,
    IReadOnlyList<string> Command,
    IReadOnlyDictionary<string, string>? Environment,
    Action<OpenCodeServerEnsureReason, string?>? OnStart,
    OpenCodeServerVersionPolicy Policy)
{
    private const string Prefix = nameof(OpenCodeServerEnsureOptions) + ".";

    /// <summary>Gets the version the election filters by: none under Ignore, the expected one otherwise.</summary>
    public string? LoopVersion => Policy == OpenCodeServerVersionPolicy.Ignore ? null : Selection.ExpectedVersion;

    /// <summary>Validates and copies the caller's options.</summary>
    /// <param name="options">The public options; null means every default.</param>
    /// <returns>The snapshot.</returns>
    /// <exception cref="ArgumentException">A value is blank, contradictory, or missing for its policy, or a command or environment entry holds a NUL.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The policy is not a defined value.</exception>
    public static EnsureRequest Snapshot(OpenCodeServerEnsureOptions? options)
    {
        var selection = ServiceSelection.Snapshot(options);
        if (options is null)
        {
            return new EnsureRequest(selection, OpenCodeServerEnsureOptions.DefaultCommand, null, null, OpenCodeServerVersionPolicy.Ignore);
        }

        var policy = options.VersionPolicy;
        if (policy is not (OpenCodeServerVersionPolicy.Ignore or OpenCodeServerVersionPolicy.Replace or OpenCodeServerVersionPolicy.Error))
        {
            throw new ArgumentOutOfRangeException(nameof(options), policy, Prefix + "VersionPolicy is not a defined policy.");
        }

        if (policy != OpenCodeServerVersionPolicy.Ignore && selection.ExpectedVersion is null)
        {
            throw new ArgumentException(
                Prefix + "VersionPolicy " + policy + " needs ExpectedVersion; without it there is nothing to mismatch.",
                nameof(options));
        }

        return new EnsureRequest(
            selection,
            CopyCommand(options.Command, nameof(options)),
            CopyEnvironment(options.Environment, nameof(options)),
            options.OnStart,
            policy);
    }

    private static string[] CopyCommand(IReadOnlyList<string>? command, string paramName)
    {
        if (command is not { Count: > 0 })
        {
            throw new ArgumentException(Prefix + "Command needs the executable and its leading arguments.", paramName);
        }

        var copy = new string[command.Count];
        for (var index = 0; index < command.Count; index++)
        {
            var entry = command[index];
            if (string.IsNullOrWhiteSpace(entry))
            {
                throw new ArgumentException(Prefix + "Command entries cannot be blank.", paramName);
            }

            if (ContainsNul(entry))
            {
                throw new ArgumentException(Prefix + "Command entries cannot contain NUL.", paramName);
            }

            copy[index] = entry;
        }

        return copy;
    }

    private static Dictionary<string, string>? CopyEnvironment(IReadOnlyDictionary<string, string>? environment, string paramName)
    {
        if (environment is null)
        {
            return null;
        }

        var copy = new Dictionary<string, string>(environment.Count, StringComparer.Ordinal);
        foreach (var entry in environment)
        {
            if (string.IsNullOrWhiteSpace(entry.Key) || ContainsNul(entry.Key) || entry.Value is null || ContainsNul(entry.Value))
            {
                throw new ArgumentException(
                    Prefix + "Environment keys cannot be blank, values cannot be null, and neither can contain NUL.",
                    paramName);
            }

            copy[entry.Key] = entry.Value;
        }

        return copy;
    }

    /// <summary>The NUL scan the analyzer wall leaves (the <c>ServiceContenderSpawner.ContainsNul</c> precedent).</summary>
    private static bool ContainsNul(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\0')
            {
                return true;
            }
        }

        return false;
    }
}
