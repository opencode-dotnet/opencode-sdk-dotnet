using System.Text;

namespace OpenCode.Sdk.Internal.Windows;

/// <summary>
/// The <c>CreateProcessW</c> Unicode environment block: <c>key=value</c> entries, each ending in
/// NUL, ordered case-insensitively by name, with one extra NUL ending the block. The order is the
/// documented contract .NET's own <c>Process</c> and libuv both follow; the child's runtime
/// updates its environment in place assuming it, so an unordered block can leave a duplicate or
/// an unreplaced entry behind.
/// </summary>
internal sealed class WindowsEnvironmentBlock
{
    /// <summary>Builds the block from the merged environment.</summary>
    /// <param name="environment">The launching process's environment with the overlay applied.</param>
    /// <returns>The block, terminators included.</returns>
    public static string Build(IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var block = new StringBuilder();
        foreach (var entry in environment.OrderBy(static entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            _ = block.Append(entry.Key).Append('=').Append(entry.Value).Append('\0');
        }

        return block.Append('\0').ToString();
    }
}
