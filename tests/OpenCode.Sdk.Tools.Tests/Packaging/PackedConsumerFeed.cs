using System.Globalization;
using System.Text;
using OpenCode.Sdk.Tools.Generator.Refresh;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions;
using TUnit.Core.Interfaces;

namespace OpenCode.Sdk.Tools.Tests.Packaging;

/// <summary>
/// The repository's packages, packed once per test session from the Release build the gate has
/// already run, into a feed of their own; and throwaway consumer projects built against them, the
/// way a consumer meets the published packages. Everything lives under one temporary root, which
/// stops MSBuild's upward search for build files and carries the repository's SDK pin, so a
/// consumer sees only what it is given. A consumer restores offline: its NuGet.Config clears every
/// inherited source and names only the feed and the machine's global packages folder, into a
/// packages folder of its own, so the packed versions never reach the global folder and a package
/// the build-time restore did not fetch fails the restore by name instead of reaching the network.
/// </summary>
public sealed class PackedConsumerFeed : IAsyncInitializer, IAsyncDisposable
{
    /// <summary>The prerelease suffix the packed versions carry, so no consumer can resolve a published one.</summary>
    private const string VersionSuffix = "packed-consumer";

    private static readonly RealFileSystem FileSystem = new();

    private static readonly string[] PackedProjects =
    [
        "src/OpenCode.Sdk/OpenCode.Sdk.csproj",
        "src/OpenCode.Sdk.Extensions/OpenCode.Sdk.Extensions.csproj",
    ];

    private readonly CliWrapProcessRunner _runner = new();
    private string? _root;
    private int _consumers;

    /// <summary>Gets the version every packed package carries.</summary>
    public string Version { get; private set; } = string.Empty;

    /// <summary>Gets the directory the packed packages are in.</summary>
    public string FeedDirectory => FileSystem.Path.Combine(Root, "feed");

    private string Root => _root ?? throw new InvalidOperationException("The feed is not initialized.");

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        var repository = new RepositoryRoot(FileSystem).Locate();
        _root = FileSystem.Path.Combine(FileSystem.Path.GetTempPath(), "opencode-packed-consumer-" + Guid.NewGuid().ToString("N"));
        var feed = FileSystem.Path.Combine(_root, "feed");
        _ = FileSystem.Directory.CreateDirectory(feed);

        // The root's own build files end MSBuild's upward search here, and its global.json is the
        // repository's, so every consumer builds on the SDK the repository pins.
        await FileSystem.File.WriteAllTextAsync(FileSystem.Path.Combine(_root, "Directory.Build.props"), "<Project />").ConfigureAwait(false);
        await FileSystem.File.WriteAllTextAsync(FileSystem.Path.Combine(_root, "Directory.Build.targets"), "<Project />").ConfigureAwait(false);
        FileSystem.File.Copy(FileSystem.Path.Combine(repository, "global.json"), FileSystem.Path.Combine(_root, "global.json"));

        var globalPackages = await GlobalPackagesFolderAsync().ConfigureAwait(false);
        await FileSystem.File.WriteAllTextAsync(FileSystem.Path.Combine(_root, "NuGet.Config"), NuGetConfig(feed, globalPackages)).ConfigureAwait(false);

        foreach (var project in PackedProjects)
        {
            // The local packed-consumer gate stays offline: baseline validation may need nuget.org.
            var packed = await RunAsync(
                ["pack", FileSystem.Path.Combine(repository, project), "--configuration", "Release", "--no-build",
                    "--output", feed, "-p:VersionSuffix=" + VersionSuffix, "-p:EnablePackageValidation=false", "-nodeReuse:false"],
                repository).ConfigureAwait(false);
            if (packed.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "Packing " + project + " failed; the packed-consumer tests need the Release build the gate runs first.\n" + packed.Text);
            }
        }

        var package = FileSystem.Directory.EnumerateFiles(feed, "OpenCodeDotNet.Sdk.*.nupkg")
            .Select(FileSystem.Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Single(name => !name.StartsWith("OpenCodeDotNet.Sdk.Extensions.", StringComparison.Ordinal) && !name.EndsWith(".symbols", StringComparison.Ordinal));
        Version = package["OpenCodeDotNet.Sdk.".Length..];
    }

    /// <summary>Restores and builds one consumer of one package on one target framework.</summary>
    /// <param name="packageId">The package the consumer references.</param>
    /// <param name="targetFramework">The consumer's target framework.</param>
    /// <param name="suppress">Whether the consumer sets <c>SuppressTfmSupportBuildWarnings</c>.</param>
    /// <param name="cancellationToken">The test's token.</param>
    /// <returns>The build's exit code and output.</returns>
    public async Task<ConsumerBuild> BuildConsumerAsync(string packageId, string targetFramework, bool suppress, CancellationToken cancellationToken)
    {
        var directory = FileSystem.Path.Combine(
            Root, "consumer-" + Interlocked.Increment(ref _consumers).ToString(CultureInfo.InvariantCulture));
        _ = FileSystem.Directory.CreateDirectory(directory);
        var project = FileSystem.Path.Combine(directory, "Consumer.csproj");
        await FileSystem.File.WriteAllTextAsync(project, ConsumerProject(packageId, targetFramework, suppress), cancellationToken).ConfigureAwait(false);

        var built = await RunAsync(
            ["build", project, "-nologo", "-nodeReuse:false", "-tl:off", "-v:q", "-clp:NoSummary",
                "-p:RestoreConfigFile=" + FileSystem.Path.Combine(Root, "NuGet.Config")],
            directory,
            cancellationToken).ConfigureAwait(false);
        return new ConsumerBuild(built.ExitCode, built.Text);
    }

    /// <inheritdoc />
    [SlopwatchSuppress(
        "SW003",
        "Disposal is best effort: a consumer's build server may still hold a file for a moment, and a leftover temporary directory must not fail the session.")]
    public ValueTask DisposeAsync()
    {
        if (_root is { } root && FileSystem.Directory.Exists(root))
        {
            try
            {
                FileSystem.Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: see the suppression.
            }
            catch (UnauthorizedAccessException)
            {
                // Best effort: see the suppression.
            }
        }

        return ValueTask.CompletedTask;
    }

    private static string NuGetConfig(string feed, string globalPackages) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <packageSources>
            <clear />
            <add key="packed" value="{feed}" />
            <add key="global-packages" value="{globalPackages}" />
          </packageSources>
        </configuration>
        """;

    private string ConsumerProject(string packageId, string targetFramework, bool suppress)
    {
        var suppression = suppress ? "<SuppressTfmSupportBuildWarnings>true</SuppressTfmSupportBuildWarnings>" : string.Empty;
        return $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>{targetFramework}</TargetFramework>
                <ImplicitUsings>disable</ImplicitUsings>
                <CheckEolTargetFramework>false</CheckEolTargetFramework>
                <DisableTransitiveFrameworkReferenceDownloads>true</DisableTransitiveFrameworkReferenceDownloads>
                <RestorePackagesPath>{FileSystem.Path.Combine(Root, "packages")}</RestorePackagesPath>
                {suppression}
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="{packageId}" Version="{Version}" />
              </ItemGroup>
            </Project>
            """;
    }

    private async Task<string> GlobalPackagesFolderAsync()
    {
        var listed = await RunAsync(["nuget", "locals", "global-packages", "--list"], Root).ConfigureAwait(false);
        const string prefix = "global-packages:";
        var line = listed.Text.Split('\n').Select(static text => text.Trim()).FirstOrDefault(text => text.StartsWith(prefix, StringComparison.Ordinal));
        return line is null
            ? throw new InvalidOperationException("The global packages folder could not be read:\n" + listed.Text)
            : line[prefix.Length..].Trim();
    }

    private async Task<(int ExitCode, string Text)> RunAsync(IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync("dotnet", arguments, workingDirectory, cancellationToken).ConfigureAwait(false);
        return (result.ExitCode, Encoding.UTF8.GetString(result.StandardOutput) + result.StandardError);
    }
}
