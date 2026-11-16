namespace OpenCode.Sdk.Internal;

/// <summary>Runs an owned CLI child to its end, with the launcher's cleanup and diagnostics.</summary>
internal interface IServiceCliRunner
{
    /// <summary>Runs a resolved command, throwing for every end except an ordinary exit zero.</summary>
    /// <param name="executable">The resolved command.</param>
    /// <param name="arguments">The copied arguments after the executable.</param>
    /// <param name="environment">The copied overlay; null adds none.</param>
    /// <param name="cancellationToken">The caller's token; cancellation ends and releases the child.</param>
    /// <returns>The completed run, with no output returned.</returns>
    public Task RunAsync(ResolvedExecutable executable, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken);
}
