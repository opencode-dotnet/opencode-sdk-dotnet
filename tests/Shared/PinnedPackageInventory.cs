using System.IO.Abstractions;
using System.Text.Json;

namespace OpenCode.Sdk.Tests.Harness;

/// <summary>
/// Every package name the pinned snapshot declares: the workspace packages under the submodule's
/// <c>packages/</c> directory, plus every dependency their manifests name. Fail-fast by design
/// (ADR-0022): a missing submodule is an instructive error, never a skip.
/// </summary>
/// <remarks>
/// This answers whether a name exists at the accepted pin. It deliberately does not answer whether
/// that name resolves from a particular directory, which an install decides and a stale
/// <c>node_modules</c> can answer wrongly in both directions.
/// </remarks>
internal sealed class PinnedPackageInventory
{
    private static readonly string[] DependencySections =
    [
        "dependencies", "devDependencies", "peerDependencies", "optionalDependencies",
    ];

    private readonly HashSet<string> _names;

    private PinnedPackageInventory(HashSet<string> names) => _names = names;

    public static async Task<PinnedPackageInventory> LoadAsync(
        IFileSystem fileSystem, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        var packages = fileSystem.Path.Combine(
            new PinnedServerCommand(fileSystem).RepositoryRoot, "external", "opencode", "packages");
        if (!fileSystem.Directory.Exists(packages))
        {
            throw new InvalidOperationException(
                $"The pinned server source is missing at '{packages}'. Run: git submodule update --init external/opencode");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in fileSystem.Directory.GetDirectories(packages))
        {
            var manifest = fileSystem.Path.Combine(directory, "package.json");
            if (fileSystem.File.Exists(manifest))
            {
                using var stream = fileSystem.File.OpenRead(manifest);
                using var reader = new StreamReader(stream);
                AddNames(names, await reader.ReadToEndAsync(cancellationToken));
            }
        }

        return new PinnedPackageInventory(names);
    }

    public bool Declares(string packageName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);

        return _names.Contains(packageName);
    }

    private static void AddNames(HashSet<string> names, string manifestText)
    {
        using var manifest = JsonDocument.Parse(manifestText);
        var root = manifest.RootElement;
        if (root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
        {
            names.Add(name.GetString()!);
        }

        foreach (var section in DependencySections)
        {
            if (root.TryGetProperty(section, out var dependencies) &&
                dependencies.ValueKind == JsonValueKind.Object)
            {
                foreach (var dependency in dependencies.EnumerateObject())
                {
                    names.Add(dependency.Name);
                }
            }
        }
    }
}
