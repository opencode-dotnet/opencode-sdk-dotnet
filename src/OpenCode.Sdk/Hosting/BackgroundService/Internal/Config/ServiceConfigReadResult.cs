namespace OpenCode.Sdk.Internal;

/// <summary>A decoded document, a refused path, or absence when both are null.</summary>
/// <param name="Document">The readable, possibly repaired configuration.</param>
/// <param name="RefusedPath">The current or donor path the strict reader cannot decode but the CLI may read.</param>
internal sealed record ServiceConfigReadResult(ServiceConfigDocument? Document, string? RefusedPath)
{
    /// <summary>No configuration readable by the CLI is available.</summary>
    internal static ServiceConfigReadResult Absent { get; } = new(null, null);
}
