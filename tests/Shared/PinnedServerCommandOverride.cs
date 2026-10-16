namespace OpenCode.Sdk.Tests.Harness;

/// <summary>
/// The owned fixture's server-command override, resolved once from
/// <c>OPENCODE_SDK_TESTS_SERVER_COMMAND</c>: the command the fixture starts instead of the pinned
/// source run, with every other part of the owned path unchanged. The distributed-build consumer
/// leg is its standing user - it points the same fixture at the published
/// <c>@opencode/cli</c> build with <c>opencode|serve</c>.
/// </summary>
/// <remarks>
/// The value is <c>|</c>-separated rather than space-separated so a path with spaces survives.
/// Tokens are handed to the
/// launcher exactly as written: resolving the executable from <c>PATH</c> (with <c>PATHEXT</c> on
/// Windows, which is what starts an npm <c>.cmd</c> shim) is the shipped launcher's job, not this
/// type's, so a bare <c>opencode</c> is a correct value and is never probed here.
/// </remarks>
internal sealed class PinnedServerCommandOverride
{
    private static readonly char[] Separator = ['|'];

    private PinnedServerCommandOverride(IReadOnlyList<string> command) => Command = command;

    /// <summary>The command tokens, in launcher order: the executable followed by its arguments.</summary>
    internal IReadOnlyList<string> Command { get; }

    /// <summary>
    /// Resolves the override from the real process environment through
    /// <see cref="Environment.GetEnvironmentVariable(string)"/>.
    /// </summary>
    internal static PinnedServerCommandOverride? FromEnvironment() =>
        FromEnvironment(Environment.GetEnvironmentVariable);

    /// <summary>
    /// Test seam: resolves the override through an injected reader instead of the real process
    /// environment, so the resolution rules are testable without mutating ambient state.
    /// </summary>
    /// <param name="read">
    /// Reads one named environment variable; mirrors
    /// <see cref="Environment.GetEnvironmentVariable(string)"/> (<see langword="null"/> for
    /// unset).
    /// </param>
    /// <returns>
    /// The override when the variable is set; <see langword="null"/> when it is unset, which is
    /// the pinned source run.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The variable is set but does not spell a command: it is blank, it is separators alone, or
    /// one of its tokens is blank.
    /// </exception>
    internal static PinnedServerCommandOverride? FromEnvironment(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        var value = read("OPENCODE_SDK_TESTS_SERVER_COMMAND");
        if (value is null)
        {
            return null;
        }

        // A variable set to blank, to separators alone, or with a blank token reads as a
        // declaration the run cannot act on. Falling back to the pinned source run would answer a
        // question the operator did not ask, so it fails by name instead.
        var tokens = value.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length is 0 || Array.Exists(tokens, string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException(
                "OPENCODE_SDK_TESTS_SERVER_COMMAND must be a '|'-separated command whose tokens are all non-blank " +
                "(for example 'opencode|serve'); leave it unset to run the pinned server from source.");
        }

        return new PinnedServerCommandOverride(tokens);
    }

    /// <summary>Renders the command the way a shell would show it, for the fixture's start banner.</summary>
    public override string ToString() => string.Join(' ', Command);
}
