using System.Diagnostics;
using System.Globalization;
using System.IO.Abstractions;
using System.Runtime.InteropServices;
using System.Text.Json;
using OpenCode.Sdk.Internal;
using Testably.Abstractions;

namespace OpenCode.Sdk.TestSupport;

/// <summary>
/// What every source-run server in this assembly needs before its first test: the pinned submodule
/// checked out, its dependencies installed, and on PATH the Bun the pin's <c>packageManager</c>
/// names. It is checked once, before discovery, because TUnit reports a failed discovery hook as one
/// error for the module, where a failed session hook fails every test at 0 ms with the same message.
/// A missing piece refuses the whole module, a run filtered to tests that start no server included:
/// every gate and CI leg prepares the checkout, so an unprepared one is a mistake to name before it
/// costs a run, not a mode to support. One exception is explicit: <see cref="BunMismatchVariable"/>
/// set to <see cref="AllowBunMismatch"/> lets another Bun run, and every module that runs on it says
/// so. CI never sets it.
/// </summary>
public static class PinnedServerPrerequisites
{
    /// <summary>The variable that lets the module run on a Bun other than the pinned one.</summary>
    internal const string BunMismatchVariable = "OPENCODE_SDK_TESTS_BUN_MISMATCH";

    /// <summary>The one value <see cref="BunMismatchVariable"/> takes.</summary>
    internal const string AllowBunMismatch = "allow";

    private const string BunPackageManagerPrefix = "bun@";

    [Before(TestDiscovery)]
    public static async Task Require_A_Prepared_Checkout_Async(CancellationToken cancellationToken)
    {
        var (repositoryRoot, readiness) = await CheckAsync(cancellationToken).ConfigureAwait(false);
        if (readiness.Problems.Count > 0)
        {
            throw new InvalidOperationException(Describe(ModuleName(), repositoryRoot, readiness.Problems));
        }
    }

    /// <summary>
    /// Names, once per module, the Bun the discovery guard let run off the pin. The check runs again
    /// here because TUnit keeps a session hook's output, in the TRX and under <c>--output
    /// Detailed</c>, and drops a discovery hook's.
    /// </summary>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>A task that completes once the mismatch, if any, is written.</returns>
    [Before(TestSession)]
    public static async Task Report_An_Allowed_Bun_Mismatch_Async(CancellationToken cancellationToken)
    {
        var (_, readiness) = await CheckAsync(cancellationToken).ConfigureAwait(false);
        if (readiness.AllowedMismatch is { } mismatch)
        {
            await Console.Out.WriteLineAsync((ModuleName() + ": " + mismatch).AsMemory(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Everything that keeps the pinned server from starting, each with the command that fixes it.</summary>
    /// <param name="fileSystem">The filesystem the checkout lives on.</param>
    /// <param name="repositoryRoot">This repository's root.</param>
    /// <param name="bunVersion">What <c>bun --version</c> printed, or null when no Bun is on PATH.</param>
    /// <param name="bunMismatch">The value of <see cref="BunMismatchVariable"/>, or null when it is unset.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>
    /// The problems in the order to fix them, empty when the checkout is prepared, and the Bun
    /// mismatch the variable allowed, if any.
    /// </returns>
    internal static async Task<Readiness> FindAsync(
        IFileSystem fileSystem,
        string repositoryRoot,
        string? bunVersion,
        string? bunMismatch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        var problems = new List<string>();
        var allowMismatch = string.Equals(bunMismatch, AllowBunMismatch, StringComparison.Ordinal);
        if (!allowMismatch && !string.IsNullOrEmpty(bunMismatch))
        {
            problems.Add(
                $"{BunMismatchVariable} is '{bunMismatch}', but the only value it takes is '{AllowBunMismatch}'. "
                + "Unset it to require the pinned bun.");
        }

        var submodule = Submodule(fileSystem, repositoryRoot);
        if (MissingSource(fileSystem, submodule) is { } source)
        {
            // Without the checkout neither the install nor the pinned Bun version can be read.
            problems.Add(source);
            return new Readiness(problems, AllowedMismatch: null);
        }

        string? allowedMismatch = null;
        var pinned = await PinnedBunVersionAsync(fileSystem, submodule, cancellationToken).ConfigureAwait(false);
        if (pinned.Problem is { } unpinned)
        {
            problems.Add(unpinned);
        }
        else if (bunVersion is null)
        {
            problems.Add($"No bun is on PATH. Install bun {pinned.Version}, the pin's packageManager.");
        }
        else if (!string.Equals(bunVersion, pinned.Version, StringComparison.Ordinal))
        {
            if (allowMismatch)
            {
                allowedMismatch = $"pinned bun {pinned.Version}, running bun {bunVersion} ({BunMismatchVariable}={AllowBunMismatch})";
            }
            else
            {
                problems.Add(
                    $"The bun on PATH is {bunVersion}, but the pin's packageManager is bun@{pinned.Version} "
                    + $"('{pinned.PackageJson}'). Put bun {pinned.Version} first on PATH.");
            }
        }

        if (MissingInstall(fileSystem, submodule) is { } install)
        {
            problems.Add(install);
        }

        return new Readiness(problems, allowedMismatch);
    }

    /// <summary>The refusal when the pinned server source is not checked out, or null when it is.</summary>
    internal static string? MissingSource(IFileSystem fileSystem, string submodule) =>
        fileSystem.File.Exists(Entry(fileSystem, submodule))
            ? null
            : $"The pinned server source is missing at '{Entry(fileSystem, submodule)}'. Run: git submodule update --init --depth 1 external/opencode";

    /// <summary>The refusal when the pinned server's dependencies are not installed, or null when they are.</summary>
    internal static string? MissingInstall(IFileSystem fileSystem, string submodule) =>
        fileSystem.Directory.Exists(fileSystem.Path.Combine(submodule, "node_modules"))
            ? null
            : $"The pinned server dependencies are not installed under '{submodule}'. Run there, with the pinned bun: bun install --frozen-lockfile --ignore-scripts";

    internal static string Submodule(IFileSystem fileSystem, string repositoryRoot) =>
        fileSystem.Path.Combine(repositoryRoot, "external", "opencode");

    internal static string Entry(IFileSystem fileSystem, string submodule) =>
        fileSystem.Path.Combine(submodule, "packages", "cli", "src", "index.ts");

    internal static string Describe(string module, string repositoryRoot, IReadOnlyList<string> problems) =>
        $"{module} cannot run: the pinned server its tests start is not prepared in '{repositoryRoot}'."
        + string.Concat(problems.Select(static problem => Environment.NewLine + "  - " + problem));

    private static async Task<(string RepositoryRoot, Readiness Readiness)> CheckAsync(CancellationToken cancellationToken)
    {
        var fileSystem = new RealFileSystem();
        var repositoryRoot = new PinnedServerCommand(fileSystem).RepositoryRoot;
        var bunVersion = await ReadBunVersionAsync(cancellationToken).ConfigureAwait(false);
        var readiness = await FindAsync(
            fileSystem,
            repositoryRoot,
            bunVersion,
            Environment.GetEnvironmentVariable(BunMismatchVariable),
            cancellationToken).ConfigureAwait(false);
        return (repositoryRoot, readiness);
    }

    private static string ModuleName() =>
        typeof(PinnedServerPrerequisites).Assembly.GetName().Name + " on " + RuntimeInformation.FrameworkDescription;

    private static async Task<PinnedBun> PinnedBunVersionAsync(IFileSystem fileSystem, string submodule, CancellationToken cancellationToken)
    {
        var packageJson = fileSystem.Path.Combine(submodule, "package.json");
        if (!fileSystem.File.Exists(packageJson))
        {
            return new PinnedBun(packageJson, null, $"The pin's '{packageJson}' is missing, so its Bun version is unknown. Run: git submodule update --init --depth 1 external/opencode");
        }

        using var stream = fileSystem.File.OpenRead(packageJson);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement.TryGetProperty("packageManager", out var packageManager)
               && packageManager.GetString() is { } value
               && value.StartsWith(BunPackageManagerPrefix, StringComparison.Ordinal)
            ? new PinnedBun(packageJson, value[BunPackageManagerPrefix.Length..], null)
            : new PinnedBun(packageJson, null, $"The pin's '{packageJson}' names no bun@ packageManager, so the Bun to run is unknown.");
    }

    /// <summary>
    /// What <c>bun --version</c> prints, for the Bun the fixtures start: resolved from PATH the way
    /// the launcher resolves it, through cmd.exe when that is a batch shim. Null when none is on PATH.
    /// </summary>
    private static async Task<string?> ReadBunVersionAsync(CancellationToken cancellationToken)
    {
        ResolvedExecutable bun;
        try
        {
            bun = new ExecutableResolver(ExecutableSearchEnvironment.ForCurrentProcess()).Resolve("bun");
        }
        catch (OpenCodeServerException)
        {
            return null;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = bun.IsBatchScript ? "cmd.exe" : bun.Path,
                Arguments = bun.IsBatchScript ? "/d /c \"\"" + bun.Path + "\" --version\"" : "--version",
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            },
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"'{bun.Path} --version' did not start.");
        }

        // The readers drain on their own token: cancelling them first could fill a pipe the child
        // still writes to.
        var standardOutput = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var standardError = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            process.Kill();
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        var output = await standardOutput.ConfigureAwait(false);
        var error = await standardError.ConfigureAwait(false);
        return process.ExitCode is 0
            ? output.Trim()
            : throw new InvalidOperationException(
                $"'{bun.Path} --version' exited with code {process.ExitCode.ToString(CultureInfo.InvariantCulture)}: {error.Trim()}");
    }

    /// <summary>What the pinned server still needs, and the Bun mismatch the opt-in allowed.</summary>
    /// <param name="Problems">The problems in the order to fix them; empty when the checkout is prepared.</param>
    /// <param name="AllowedMismatch">The pinned and the running Bun when the opt-in let them differ, or null.</param>
    internal sealed record Readiness(IReadOnlyList<string> Problems, string? AllowedMismatch);

    private sealed record PinnedBun(string PackageJson, string? Version, string? Problem);
}
