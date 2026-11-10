using System.Globalization;
using System.IO.Abstractions;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenCode.Sdk.Tools.Generator.Refresh.Abstractions;

namespace OpenCode.Sdk.Tools.Output;

/// <summary>Owns an isolated repository mirror for read-only generation verification.</summary>
internal sealed class GenerationMirror(IFileSystem fileSystem, IProcessRunner processRunner, ILogger<GenerationMirror> logger)
{
    private const string Marker = ".opencode-generate-verify";
    private const string MarkerContent = "opencode-sdk-generate-verify-v1\n";
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IProcessRunner _processRunner = processRunner;

    public async Task<WriteResult> RunAsync(GenerationWriteRequest request,
        Func<GenerationWriteRequest, Task<WriteResult>> generate, CancellationToken cancellationToken)
    {
        var repository = _fileSystem.Directory.GetCurrentDirectory();
        var relativeOutput = RepositoryPath(repository, request.OutputRoot);
        var relativeProject = RepositoryPath(repository, request.ProjectPath);
        var parent = _fileSystem.Path.Combine(_fileSystem.Path.GetTempPath(), "opencode-sdk-generate-verify");
        GuardContext(parent);
        _fileSystem.Directory.CreateDirectory(parent);
        var leftovers = _fileSystem.Directory.EnumerateDirectories(parent)
            .Count(directory => _fileSystem.File.Exists(_fileSystem.Path.Combine(directory, Marker)));
        if (leftovers != 0)
        {
            logger.LogWarning("{Count} marked directories under '{Parent}'; they may belong to a verify that is still running or was interrupted; remove them by hand when no verify is running", leftovers, parent);
        }
        var root = _fileSystem.Path.Combine(parent, Guid.NewGuid().ToString("N"));
        _fileSystem.Directory.CreateDirectory(root);
        try
        {
            await _fileSystem.File.WriteAllTextAsync(_fileSystem.Path.Combine(root, Marker), MarkerContent, cancellationToken).ConfigureAwait(false);
            var listing = await _processRunner.RunAsync("git", ["ls-files", "-z"], repository, cancellationToken).ConfigureAwait(false);
            if (listing.ExitCode != 0)
            {
                throw new OutputRefusalException($"'git ls-files' failed (exit {listing.ExitCode.ToString(CultureInfo.InvariantCulture)}): {listing.StandardError.Trim()}");
            }

            foreach (var file in Encoding.UTF8.GetString(listing.StandardOutput).Split('\0', StringSplitOptions.RemoveEmptyEntries)
                         .Where(static path => !path.Contains('/', StringComparison.Ordinal)))
            {
                _fileSystem.File.Copy(_fileSystem.Path.Combine(repository, file), _fileSystem.Path.Combine(root, file));
            }

            var output = _fileSystem.Path.Combine(root, relativeOutput);
            CopyProject(request.OutputRoot, output, cancellationToken);
            var project = _fileSystem.Path.Combine(root, relativeProject);
            var source = _fileSystem.Path.Combine(root, "empty-source");
            _fileSystem.Directory.CreateDirectory(source);
            var restore = await _processRunner.RunAsync("dotnet", ["restore", project, "--source", source], repository, cancellationToken).ConfigureAwait(false);
            if (restore.ExitCode != 0)
            {
                throw new OutputRefusalException($"Staging project restore failed (exit {restore.ExitCode.ToString(CultureInfo.InvariantCulture)}): {Encoding.UTF8.GetString(restore.StandardOutput).Trim()}\n{restore.StandardError.Trim()}\nRestore the real project once ('dotnet restore {request.ProjectPath}') so the global packages folder holds every package.");
            }

            return await generate(request with { OutputRoot = output, ProjectPath = project }).ConfigureAwait(false);
        }
        finally
        {
            _fileSystem.Directory.Delete(root, recursive: true);
        }
    }

    private string RepositoryPath(string repository, string path)
    {
        var relative = _fileSystem.Path.GetRelativePath(repository, path);
        if (_fileSystem.Path.IsPathRooted(relative) || relative.Split(_fileSystem.Path.DirectorySeparatorChar)[0] == "..")
        {
            throw new OutputRefusalException($"Path '{path}' is outside the repository.");
        }

        return relative;
    }

    private void GuardContext(string parent)
    {
        for (var directory = _fileSystem.DirectoryInfo.New(parent); directory is not null; directory = directory.Parent)
        {
            foreach (var name in (string[])[".globalconfig", ".editorconfig", "Directory.Build.props", "Directory.Build.targets",
                         "Directory.Packages.props", "Directory.Build.rsp", "global.json", "nuget.config", "NuGet.Config", "NuGet.config"])
            {
                var path = _fileSystem.Path.Combine(directory.FullName, name);
                if (_fileSystem.File.Exists(path))
                {
                    throw new OutputRefusalException($"Staging context contains '{path}'; point the temp directory elsewhere through TMPDIR (Unix) or TEMP/TMP (Windows).");
                }
            }
        }
    }

    private void CopyProject(string source, string destination, CancellationToken cancellationToken)
    {
        foreach (var file in _fileSystem.Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = _fileSystem.Path.GetRelativePath(source, file);
            if (relative.Split(_fileSystem.Path.DirectorySeparatorChar).Any(static part =>
                    StringComparer.OrdinalIgnoreCase.Equals(part, "bin") || StringComparer.OrdinalIgnoreCase.Equals(part, "obj")))
            {
                continue;
            }

            var target = _fileSystem.Path.Combine(destination, relative);
            // A copied file always has its project directory as a parent.
            _fileSystem.Directory.CreateDirectory(_fileSystem.Path.GetDirectoryName(target)!);
            _fileSystem.File.Copy(file, target);
        }
    }
}
