using System.Globalization;
using System.IO.Abstractions;
using System.Reflection;
using System.Text;
using OpenCode.Sdk.Tools.Generator.Refresh.Abstractions;
using OpenCode.Sdk.Tools.Output.Abstractions;

namespace OpenCode.Sdk.Tools.Output;

/// <summary>
/// Formats the generated tree in one <c>dotnet-format</c> run. One run costs about a minute
/// whatever it covers, so the file list travels in a response file instead of on the command
/// line, where Windows caps a child process at 32,767 UTF-16 characters (<c>CreateProcessW</c>).
/// </summary>
/// <remarks>
/// <c>dotnet format</c> cannot carry that response file: the SDK CLI expands it and then starts
/// <c>dotnet-format.dll</c> as a new process with the expanded list ("the filename or extension
/// is too long"). The formatter therefore starts the SDK's own <c>dotnet-format.dll</c> the way
/// the SDK's <c>FormatForwardingApp</c> does, and <c>dotnet-format</c> expands the file
/// in-process.
/// </remarks>
internal sealed class CliWrapProjectFormatter(IFileSystem fileSystem, IProcessRunner processRunner, string? targetFramework = null) : IProjectFormatter
{
    private const string AssemblyFileName = "dotnet-format.dll";
    private const string DepsFileName = "dotnet-format.deps.json";
    private const string RuntimeConfigFileName = "dotnet-format.runtimeconfig.json";
    private static readonly string? DefaultTargetFramework = typeof(CliWrapProjectFormatter).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().SingleOrDefault(static item => item.Key == "DefaultTargetFramework")?.Value;

    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly IProcessRunner _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));

    public async Task FormatAsync(
        string projectPath,
        IReadOnlyList<string> sourcePaths,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentNullException.ThrowIfNull(sourcePaths);
        if (sourcePaths.Count is 0)
        {
            return;
        }

        if (sourcePaths.Any(static sourcePath => string.IsNullOrWhiteSpace(sourcePath)))
        {
            throw new ArgumentException("Source paths cannot contain null or whitespace entries.", nameof(sourcePaths));
        }

        var framework = targetFramework ?? DefaultTargetFramework;
        if (string.IsNullOrWhiteSpace(framework))
        {
            throw new OutputRefusalException("The tool assembly has no DefaultTargetFramework metadata.");
        }

        var fullProjectPath = _fileSystem.Path.GetFullPath(projectPath);
        var projectDirectory = _fileSystem.Path.GetDirectoryName(fullProjectPath)
                               ?? throw new InvalidOperationException($"Project path '{projectPath}' has no parent directory.");
        if (!_fileSystem.File.Exists(_fileSystem.Path.Combine(projectDirectory, "obj", "project.assets.json")))
        {
            throw new OutputRefusalException(
                $"Project '{projectPath}' has not been restored; run 'dotnet restore {projectPath}' before generating because the formatter needs the restored project to remove redundant usings.");
        }

        var projectFileName = _fileSystem.Path.GetFileName(fullProjectPath);
        var formatterDirectory = await ResolveFormatterDirectoryAsync(projectDirectory, cancellationToken).ConfigureAwait(false);

        var responseFilePath = _fileSystem.Path.Combine(
            _fileSystem.Path.GetTempPath(),
            $"opencode-format-{Guid.NewGuid():N}.rsp");
        try
        {
            await _fileSystem.File.WriteAllTextAsync(responseFilePath, ResponseFileContent(sourcePaths), cancellationToken)
                .ConfigureAwait(false);
            _ = await RunDotnetAsync(
                ["exec", "--depsfile", FormatterFile(formatterDirectory, DepsFileName),
                    "--runtimeconfig", FormatterFile(formatterDirectory, RuntimeConfigFileName),
                    FormatterFile(formatterDirectory, AssemblyFileName), projectFileName, "--no-restore", $"@{responseFilePath}"],
                projectDirectory, cancellationToken,
                new Dictionary<string, string?>(StringComparer.Ordinal) { ["TargetFramework"] = framework })
                .ConfigureAwait(false);
        }
        finally
        {
            _fileSystem.File.Delete(responseFilePath);
        }
    }

    /// <summary>
    /// The response file <c>dotnet-format</c> reads: the <c>--include</c> option, then one
    /// double-quoted path per line, so a path with spaces stays one token.
    /// </summary>
    internal static string ResponseFileContent(IReadOnlyList<string> sourcePaths)
    {
        var content = new StringBuilder("--include").Append('\n');
        foreach (var sourcePath in sourcePaths)
        {
            if (sourcePath.Contains('"', StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Source path '{sourcePath}' contains a double quote, which a response-file token cannot carry.",
                    nameof(sourcePaths));
            }

            _ = content.Append('"').Append(sourcePath).Append('"').Append('\n');
        }

        return content.ToString();
    }

    /// <summary>
    /// Finds the <c>dotnet-format</c> directory inside the SDK that <c>global.json</c> selects for
    /// <paramref name="projectDirectory"/>: the SDK version from <c>dotnet --version</c>, its
    /// install root from <c>dotnet --list-sdks</c>, and the <c>DotnetTools</c> layout the SDK's
    /// own forwarder uses. The assembly, deps file, and runtime config are the three files that
    /// forwarder passes to <c>dotnet exec</c>; a missing one stops generation rather than falling
    /// back.
    /// </summary>
    private async Task<string> ResolveFormatterDirectoryAsync(string projectDirectory, CancellationToken cancellationToken)
    {
        var version = (await RunDotnetAsync(["--version"], projectDirectory, cancellationToken).ConfigureAwait(false)).Trim();
        var sdkListing = await RunDotnetAsync(["--list-sdks"], projectDirectory, cancellationToken).ConfigureAwait(false);
        var formatterDirectory = _fileSystem.Path.Combine(SdkRoot(sdkListing, version), version, "DotnetTools", "dotnet-format");
        foreach (var fileName in (string[])[AssemblyFileName, DepsFileName, RuntimeConfigFileName])
        {
            var path = FormatterFile(formatterDirectory, fileName);
            if (!_fileSystem.File.Exists(path))
            {
                throw new OutputRefusalException(
                    $"SDK {version} has no '{path}'; the generator formats through the SDK's bundled dotnet-format.");
            }
        }

        return formatterDirectory;
    }

    private string FormatterFile(string formatterDirectory, string fileName) =>
        _fileSystem.Path.Combine(formatterDirectory, fileName);

    /// <summary>
    /// Reads the install root of <paramref name="version"/> from <c>dotnet --list-sdks</c>
    /// output, whose lines read <c>10.0.303 [C:\Program Files\dotnet\sdk]</c>.
    /// </summary>
    internal static string SdkRoot(string sdkListing, string version)
    {
        ArgumentNullException.ThrowIfNull(sdkListing);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var prefix = $"{version} [";
        foreach (var line in sdkListing.Split('\n'))
        {
            var entry = line.Trim();
            if (entry.StartsWith(prefix, StringComparison.Ordinal) && entry[^1] is ']')
            {
                return entry[prefix.Length..^1];
            }
        }

        throw new OutputRefusalException($"'dotnet --list-sdks' does not list the selected SDK {version}.");
    }

    private async Task<string> RunDotnetAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var result = environment is null
            ? await _processRunner.RunAsync("dotnet", arguments, workingDirectory, cancellationToken).ConfigureAwait(false)
            : await _processRunner.RunAsync("dotnet", arguments, workingDirectory, environment, cancellationToken).ConfigureAwait(false);
        return result.ExitCode is 0
            ? Encoding.UTF8.GetString(result.StandardOutput)
            : throw new OutputRefusalException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"'dotnet {string.Join(' ', arguments)}' exited with code {result.ExitCode}: {result.StandardError}"));
    }
}
