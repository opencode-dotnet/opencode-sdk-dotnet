using System.Diagnostics;
using System.IO.Abstractions;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.TestSupport;

/// <summary>
/// Locates and runs the isolated discovery executable (<c>tests/OpenCode.Sdk.ServiceFixture</c>):
/// the process a live test launches with an environment it owns entirely, so
/// <c>DiscoverAsync</c>'s environment reading is proven without ever touching the developer's own
/// registration. Shares <see cref="PinnedServerCommand"/>'s repository-root walk, resolves the
/// build under the running test's own configuration, and refuses a missing output with the build
/// command instead of skipping.
/// </summary>
internal sealed class ServiceFixtureCommand
{
    private const string ProjectName = "OpenCode.Sdk.ServiceFixture";
    private static readonly char[] PathSeparators = ['/', '\\'];

    private readonly IFileSystem _fileSystem;
    private readonly PinnedServerCommand _repository;

    public ServiceFixtureCommand(IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _fileSystem = fileSystem;
        _repository = new PinnedServerCommand(fileSystem);
    }

    /// <summary>Resolves the host and the fixture assembly: the muxer, then the DLL.</summary>
    /// <returns>The executable and its leading argument.</returns>
    public IReadOnlyList<string> Resolve()
    {
        var configuration = Configuration();
        var binDirectory = _fileSystem.Path.Combine(_repository.RepositoryRoot, "tests", ProjectName, "bin", configuration);
        var buildHint = $"Run: dotnet build tests/{ProjectName}/{ProjectName}.csproj --configuration {configuration}";
        if (!_fileSystem.Directory.Exists(binDirectory))
        {
            throw new InvalidOperationException($"The service fixture is not built under '{binDirectory}'. {buildHint}");
        }

        // One target framework directory carries the assembly; a second one means a stale build
        // the test must not pick from silently.
        var assemblies = _fileSystem.Directory
            .EnumerateDirectories(binDirectory)
            .Select(directory => _fileSystem.Path.Combine(directory, ProjectName + ".dll"))
            .Where(_fileSystem.File.Exists)
            .ToArray();
        return assemblies switch
        {
            [var single] => [DotnetHost(), single],
            [] => throw new InvalidOperationException($"No '{ProjectName}.dll' exists beneath '{binDirectory}'. {buildHint}"),
            _ => throw new InvalidOperationException(
                $"Several '{ProjectName}.dll' builds exist beneath '{binDirectory}' ({string.Join(", ", assemblies)}); remove the stale ones."),
        };
    }

    /// <summary>
    /// Runs one fixture mode to completion with the given environment overlay applied to the
    /// inherited environment: a null value removes the variable, so a test can prove a fallback
    /// by taking a variable away rather than by hoping it is unset.
    /// </summary>
    /// <param name="arguments">The mode and its arguments.</param>
    /// <param name="environment">The overlay; null values remove.</param>
    /// <param name="cancellationToken">The caller's bound; the child is killed on cancellation.</param>
    /// <returns>The exit code and both output streams.</returns>
    public async Task<ServiceFixtureResult> RunAsync(
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(environment);

        var command = Resolve();
        var startInfo = new ProcessStartInfo
        {
            FileName = command[0],
            // The launcher's composer: ArgumentList does not exist on net472, and the composed
            // string follows the MSVCRT rules the modern runtime applies.
            Arguments = ProcessArgumentComposer.Compose(command.Skip(1).Concat(arguments)),
            WorkingDirectory = _repository.RepositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var pair in environment)
        {
            if (pair.Value is null)
            {
                _ = startInfo.Environment.Remove(pair.Key);
            }
            else
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Starting '{startInfo.FileName}' returned no process.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _ = ProcessTreeTerminator.Platform.TryKill(process);
            throw;
        }

        return new ServiceFixtureResult(
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));
    }

    private static string Configuration()
    {
        // The running test lives under <project>/bin/<Configuration>/<tfm>/; the fixture was
        // built by the same solution build, so the same configuration names its output.
        var segments = AppContext.BaseDirectory.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);
        var bin = Array.LastIndexOf(segments, "bin");
        if (bin < 0 || bin + 1 >= segments.Length)
        {
            throw new InvalidOperationException(
                $"The test base directory '{AppContext.BaseDirectory}' carries no bin/<Configuration> segment to resolve the fixture build from.");
        }

        return segments[bin + 1];
    }

    private static string DotnetHost()
    {
        // The SDK hands every child it starts the muxer that started it; a bare "dotnet" is the
        // PATH fallback for a runner started some other way.
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return host is { Length: > 0 } ? host : "dotnet";
    }
}
