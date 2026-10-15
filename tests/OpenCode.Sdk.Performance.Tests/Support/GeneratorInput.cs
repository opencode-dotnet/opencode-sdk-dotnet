#if NET
using System.IO.Abstractions;
using System.Text.Json;
using OpenCode.Sdk.Tools.Generator;
using Testably.Abstractions;

namespace OpenCode.Sdk.Performance.Tests.Support;

/// <summary>
/// The generator's real pinned input and output, as absolute paths in the repository the benchmark
/// runs from. BenchmarkDotNet runs each benchmark in a generated project under this project's
/// output, so the repository is found by walking up to the folder that holds the pinned spec.
/// </summary>
internal sealed class GeneratorInput
{
    private const string ManifestName = ".generated-manifest.json";

    private GeneratorInput(string root, IFileSystem fileSystem)
    {
        Root = root;
        SpecPath = fileSystem.Path.Combine(root, "spec", "openapi.json");
        ProfilePath = fileSystem.Path.Combine(root, "tools", "generation-profile.txt");
        CurationPath = fileSystem.Path.Combine(root, "tools", "curation.json");
        OutputRoot = fileSystem.Path.Combine(root, "src", "OpenCode.Sdk");
        ProjectPath = fileSystem.Path.Combine(OutputRoot, "OpenCode.Sdk.csproj");
    }

    /// <summary>Gets the repository root, where the generator's relative inputs resolve.</summary>
    public string Root { get; }

    public string SpecPath { get; }

    public string ProfilePath { get; }

    public string CurationPath { get; }

    public string OutputRoot { get; }

    public string ProjectPath { get; }

    public static GeneratorInput Locate()
    {
        var fileSystem = new RealFileSystem();
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (fileSystem.File.Exists(fileSystem.Path.Combine(directory, "spec", "openapi.json"))
                && fileSystem.File.Exists(fileSystem.Path.Combine(directory, "tools", "curation.json")))
            {
                return new GeneratorInput(directory, fileSystem);
            }

            directory = fileSystem.Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("No repository with spec/openapi.json was found above " + AppContext.BaseDirectory + ".");
    }

    /// <summary>The generated sources the manifest lists, as paths relative to the output root.</summary>
    public static async Task<string[]> ManifestFilesAsync(IFileSystem fileSystem, string outputRoot)
    {
        var stream = fileSystem.File.OpenRead(fileSystem.Path.Combine(outputRoot, ManifestName));
        await using (stream.ConfigureAwait(false))
        {
            using var manifest = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            return [.. manifest.RootElement.GetProperty("files").EnumerateArray().Select(static entry => entry.GetString()!)];
        }
    }

    public GenerationRequest Request(string outputRoot, bool verify) =>
        new()
        {
            SpecPath = SpecPath,
            ProfilePath = ProfilePath,
            CurationPath = CurationPath,
            OutputRoot = outputRoot,
            ProjectPath = ProjectPath,
            Verify = verify,
        };
}
#endif
