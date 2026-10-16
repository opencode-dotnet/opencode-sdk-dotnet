using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using PublicApiGenerator;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests.Harness;

/// <summary>
/// The public API of each asset a package ships, read from the build output without loading it: the asset, the framework it compiles against, and the packages it references are staged in
/// a directory of their own and read through <see cref="MetadataLoadContext"/>, which
/// PublicApiGenerator renders the same way it renders a loaded assembly. The test project's build
/// records where its package's assets, its reviewed baseline and every reference root are
/// (<c>tests/Shared/ShippedAssets.targets</c>).
/// </summary>
internal static class ShippedAssetSurface
{
    private static readonly RealFileSystem FileSystem = new();

    /// <summary>Gets the target framework of every shipped asset the build recorded.</summary>
    /// <returns>The target frameworks, such as <c>netstandard2.0</c>.</returns>
    public static IReadOnlyList<string> TargetFrameworks() =>
        [.. Assets().Select(static asset => TargetFrameworkOf(asset))];

    /// <summary>
    /// Reads what one shipped asset must expose: the reviewed baseline, less the reviewed differences
    /// between assets. The trimming and native AOT annotations and the experimental marks are public
    /// attributes only on .NET; the netstandard2.0 and net472 assets carry internal copies of them,
    /// which are not part of their public API, so those two render the same members without them.
    /// </summary>
    /// <param name="targetFramework">The asset's target framework.</param>
    /// <returns>The expected public API, with LF line ends.</returns>
    public static async Task<string> ExpectedAsync(string targetFramework)
    {
        string baseline;
        using (var stream = FileSystem.File.OpenRead(Metadata("PublicApiBaseline")))
        using (var reader = new StreamReader(stream))
        {
            baseline = Lf(await reader.ReadToEndAsync().ConfigureAwait(false));
        }

        return targetFramework is "netstandard2.0" or "net472" ? WithoutDownlevelInternalAttributes(baseline) : baseline;
    }

    /// <summary>Renders one shipped asset's public API, LF-terminated as the baseline is.</summary>
    /// <param name="targetFramework">The asset's target framework.</param>
    /// <returns>The rendered public API.</returns>
    /// <exception cref="InvalidOperationException">The asset is not built, or a reference it needs is not in the global packages folder.</exception>
    public static async Task<string> RenderAsync(string targetFramework)
    {
        var asset = Assets().Single(candidate => TargetFrameworkOf(candidate) == targetFramework);
        if (!FileSystem.File.Exists(asset))
        {
            throw new InvalidOperationException(
                $"The shipped {targetFramework} asset '{asset}' is not built: build the solution in this configuration first.");
        }

        var (coreAssembly, frameworkDirectories) = Framework(targetFramework);
        var staging = FileSystem.Path.Combine(FileSystem.Path.GetTempPath(), "opencode-shipped-api-" + Guid.NewGuid().ToString("N"));
        _ = FileSystem.Directory.CreateDirectory(staging);
        try
        {
            var framework = frameworkDirectories
                .SelectMany(directory => FileSystem.Directory.EnumerateFiles(directory, "*.dll"))
                .GroupBy(static file => FileSystem.Path.GetFileNameWithoutExtension(file), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var file in framework.Values)
            {
                FileSystem.File.Copy(file, FileSystem.Path.Combine(staging, FileSystem.Path.GetFileName(file)), overwrite: true);
            }

            var staged = FileSystem.Path.Combine(staging, FileSystem.Path.GetFileName(asset));
            FileSystem.File.Copy(asset, staged, overwrite: true);
            await StagePackagesAsync(asset, targetFramework, framework, staging).ConfigureAwait(false);

            using var context = new MetadataLoadContext(
                new PathAssemblyResolver(FileSystem.Directory.EnumerateFiles(staging, "*.dll")), coreAssembly);
            var api = context.LoadFromAssemblyPath(staged).GeneratePublicApi(new ApiGeneratorOptions
            {
                IncludeAssemblyAttributes = false,
                AllowNamespacePrefixes = ["System", "Microsoft"],
            });
            return string.Concat(Lf(api), "\n");
        }
        finally
        {
            FileSystem.Directory.Delete(staging, recursive: true);
        }
    }

    private static string[] Assets() =>
        Metadata("ShippedAssets").Split([';'], StringSplitOptions.RemoveEmptyEntries);

    private static string TargetFrameworkOf(string asset) =>
        FileSystem.Path.GetFileName(FileSystem.Path.GetDirectoryName(asset)) ?? asset;

    private static string Metadata(string key) =>
        typeof(ShippedAssetSurface).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == key)?.Value
        ?? throw new InvalidOperationException($"The test project's build recorded no '{key}'.");

    /// <summary>The core assembly and the reference directories a target framework compiles against.</summary>
    private static (string CoreAssembly, IReadOnlyList<string> Directories) Framework(string targetFramework)
    {
        var packages = Metadata("NuGetPackageRoot");
        switch (targetFramework)
        {
            case "netstandard2.0":
                return ("netstandard", [Newest(FileSystem.Path.Combine(packages, "netstandard.library"), "build/netstandard2.0/ref")]);
            case "net472":
                var framework = Newest(FileSystem.Path.Combine(packages, "microsoft.netframework.referenceassemblies.net472"), "build/.NETFramework/v4.7.2");
                return ("mscorlib", [framework, FileSystem.Path.Combine(framework, "Facades")]);
            default:
                var candidates = new[]
                    {
                        FileSystem.Path.Combine(Metadata("NetCoreTargetingPackRoot"), "Microsoft.NETCore.App.Ref"),
                        FileSystem.Path.Combine(packages, "microsoft.netcore.app.ref"),
                    }
                    .Where(FileSystem.Directory.Exists)
                    .SelectMany(FileSystem.Directory.EnumerateDirectories)
                    .Select(version => FileSystem.Path.Combine(version, "ref", targetFramework))
                    .Where(FileSystem.Directory.Exists)
                    .OrderByDescending(static directory => VersionOf(FileSystem.Path.GetFileName(FileSystem.Path.GetDirectoryName(FileSystem.Path.GetDirectoryName(directory)))))
                    .ToList();
                return candidates.Count > 0
                    ? ("System.Runtime", [candidates[0]])
                    : throw new InvalidOperationException($"No {targetFramework} reference pack was found under the SDK's packs or the global packages folder.");
        }
    }

    /// <summary>
    /// Stages every assembly the asset references, and theirs: a project reference from the asset's
    /// own build output, where the build copies it, and a package from the global packages folder.
    /// </summary>
    private static async Task StagePackagesAsync(string asset, string targetFramework, Dictionary<string, string> framework, string staging)
    {
        var pending = new Queue<string>(await ReferencesOfAsync(asset).ConfigureAwait(false));
        var staged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0)
        {
            var name = pending.Dequeue();
            if (framework.ContainsKey(name) || !staged.Add(name))
            {
                continue;
            }

            var sibling = FileSystem.Path.Combine(FileSystem.Path.GetDirectoryName(asset) ?? string.Empty, name + ".dll");
            var package = (FileSystem.File.Exists(sibling) ? sibling : PackageAssembly(name, targetFramework))
                ?? throw new InvalidOperationException(
                    $"The {targetFramework} asset references '{name}', which is neither in its framework, nor beside it, nor in the global packages folder.");
            FileSystem.File.Copy(package, FileSystem.Path.Combine(staging, FileSystem.Path.GetFileName(package)), overwrite: true);
            foreach (var reference in await ReferencesOfAsync(package).ConfigureAwait(false))
            {
                pending.Enqueue(reference);
            }
        }
    }

    private static string? PackageAssembly(string name, string targetFramework)
    {
        var root = FileSystem.Path.Combine(Metadata("NuGetPackageRoot"), name.ToLowerInvariant());
        if (!FileSystem.Directory.Exists(root))
        {
            return null;
        }

        string[] folders = targetFramework switch
        {
            "netstandard2.0" => ["netstandard2.0"],
            "net472" => ["net472", "net462", "net461", "netstandard2.0"],
            _ => [targetFramework, "net10.0", "net9.0", "net8.0", "netstandard2.1", "netstandard2.0"],
        };
        return FileSystem.Directory.EnumerateDirectories(root)
            .OrderByDescending(static version => VersionOf(FileSystem.Path.GetFileName(version)))
            .SelectMany(version => folders.Select(folder => FileSystem.Path.Combine(version, "lib", folder, name + ".dll")))
            .FirstOrDefault(FileSystem.File.Exists);
    }

    private static async Task<List<string>> ReferencesOfAsync(string assembly)
    {
        using var buffer = new MemoryStream();
        using (var stream = FileSystem.File.OpenRead(assembly))
        {
            await stream.CopyToAsync(buffer).ConfigureAwait(false);
        }

        buffer.Position = 0;
        using var reader = new PEReader(buffer);
        var metadata = reader.GetMetadataReader();
        return [.. metadata.AssemblyReferences.Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))];
    }

    private static string Newest(string packageRoot, string relative) =>
        FileSystem.Directory.EnumerateDirectories(packageRoot)
            .OrderByDescending(static version => VersionOf(FileSystem.Path.GetFileName(version)))
            .Select(version => FileSystem.Path.Combine(version, relative))
            .FirstOrDefault(FileSystem.Directory.Exists)
        ?? throw new InvalidOperationException($"No '{relative}' was found under '{packageRoot}'.");

    private static string WithoutDownlevelInternalAttributes(string api)
    {
        var kept = new List<string>();
        var inAnnotation = false;
        foreach (var line in api.Split('\n'))
        {
            var trimmed = line.TrimStart();
            inAnnotation |= trimmed.StartsWith("[System.Diagnostics.CodeAnalysis.RequiresDynamicCode(", StringComparison.Ordinal)
                || trimmed.StartsWith("[System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(", StringComparison.Ordinal)
                || trimmed.StartsWith("[System.Diagnostics.CodeAnalysis.Experimental(", StringComparison.Ordinal);
            if (!inAnnotation)
            {
                kept.Add(line);
            }
            else if (line.EndsWith(")]", StringComparison.Ordinal))
            {
                inAnnotation = false;
            }
        }

        return string.Join('\n', kept);
    }

    private static string Lf(string text) => string.Join('\n', text.Split(["\r\n"], StringSplitOptions.None));

    private static Version VersionOf(string? folder) =>
        Version.TryParse((folder ?? string.Empty).Split('-')[0], out var version) ? version : new Version(0, 0);
}
