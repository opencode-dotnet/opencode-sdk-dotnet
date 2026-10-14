using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.TestRoster.Models;

/// <summary>The committed expected roster: per test pass, per operating system, the modules that
/// must have run, each written as "Assembly/target-framework".</summary>
internal sealed record TestRosterDocument
{
    /// <summary>Pass name (for example "main") to operating-system name (for example "windows")
    /// to that leg's expected modules.</summary>
    [JsonPropertyName("passes")]
    public required IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> Passes { get; init; }

    /// <summary>Resolves the expected modules for one pass and one GitHub <c>runner.os</c> value.
    /// The OS name matches case-insensitively; a missing pass, a missing OS, or an empty leg
    /// refuses instead of verifying nothing.</summary>
    public IReadOnlyList<string> ResolveEntries(string passName, string operatingSystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passName);
        ArgumentException.ThrowIfNullOrWhiteSpace(operatingSystem);

        if (!Passes.TryGetValue(passName, out var operatingSystems))
        {
            throw new TestRosterException($"The roster names no pass '{passName}'. Known passes: {KnownNames(Passes.Keys)}.");
        }

        var operatingSystemKey = operatingSystem.ToLowerInvariant();
        if (!operatingSystems.TryGetValue(operatingSystemKey, out var modules))
        {
            throw new TestRosterException(
                $"Pass '{passName}' names no OS '{operatingSystem}'. Known OSs: {KnownNames(operatingSystems.Keys)}.");
        }

        return modules.Count > 0
            ? modules
            : throw new TestRosterException(
                $"The roster lists no modules for pass '{passName}' on '{operatingSystemKey}'; an empty roster leg would pass without verifying anything.");
    }

    private static string KnownNames(IEnumerable<string> names) =>
        string.Join(", ", names.Order(StringComparer.Ordinal).Select(static name => $"'{name}'"));
}
