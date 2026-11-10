using Microsoft.Extensions.Logging;
using OpenCode.Sdk.Tools.Generator.Refresh.Abstractions;
using OpenCode.Sdk.Tools.Infrastructure.Logging;
using OpenCode.Sdk.Tools.Output;
using OpenCode.Sdk.Tools.Output.Abstractions;
using OpenCode.Sdk.Tools.Tests.Output.Support;
using OpenCode.Sdk.Tools.Tests.Support;
using Spectre.Console.Testing;
using Testably.Abstractions.Testing;
using Testably.Abstractions.Testing.Initializer;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tools.Tests.Output.Support.VerifyMirrorScenario;

namespace OpenCode.Sdk.Tools.Tests.Output;

public sealed class GenerationMirrorTests
{
    [Test]
    [Arguments(".globalconfig")]
    [Arguments(".editorconfig")]
    [Arguments("Directory.Build.props")]
    [Arguments("Directory.Build.targets")]
    [Arguments("Directory.Packages.props")]
    [Arguments("global.json")]
    [Arguments("nuget.config")]
    [Arguments("NuGet.Config")]
    [Arguments("NuGet.config")]
    [Arguments("Directory.Build.rsp")]
    public async Task WriteAsync_Should_Refuse_Ancestor_Context_Before_Copying(string name)
    {
        foreach (var level in (int[])[0, 1, 2])
        {
            var fs = new MockFileSystem(o => o.SimulatingOperatingSystem(SimulationMode.Linux));
            var scenario = new VerifyMirrorScenario(fs);
            var parent = fs.Path.Combine(fs.Path.GetTempPath(), "opencode-sdk-generate-verify");
            var ancestor = level switch { 0 => parent, 1 => fs.Path.GetTempPath(), _ => fs.Path.GetPathRoot(parent)! };
            fs.Directory.CreateDirectory(ancestor);
            var path = fs.Path.Combine(ancestor, name);
            await fs.File.WriteAllTextAsync(path, "context");
            var before = await scenario.ReadTreeAsync();
            var runner = new ScriptedProcessRunner();
            var formatter = new RecordingProjectFormatter(fs);
            var touched = new List<string>();
            using var changes = fs.Notify.OnEvent(change => touched.Add(change.Path));

            var refusal = await RefusedVerifyAsync(scenario, formatter, runner);

            await Assert.That(refusal!.Message).IsEqualTo(
                $"Staging context contains '{fs.Path.GetFullPath(path)}'; point the temp directory elsewhere through TMPDIR (Unix) or TEMP/TMP (Windows).");
            await Assert.That(runner.Invocations).IsEmpty();
            await Assert.That(formatter.CallCount).IsEqualTo(0);
            await Assert.That(touched).IsEmpty();
            await scenario.AssertTreeUnchangedAsync(before);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WriteAsync_Should_Refuse_Paths_Outside_The_Repository_Before_Copying(bool project)
    {
        var fs = GenerationTestData.CreateFileSystem();
        fs.Directory.CreateDirectory("repository");
        fs.Directory.SetCurrentDirectory("repository");
        fs.Initialize().With(new FileDescription(GenerationTestData.ProjectPath, "project"),
            new FileDescription("../outside/manual.cs", "untouched"));
        var scenario = new VerifyMirrorScenario(fs);
        var request = project ? Request([], verify: true) with { ProjectPath = "../outside.csproj" }
            : Request([], verify: true) with { OutputRoot = "../outside" };
        var touched = new List<string>();
        using var changes = fs.Notify.OnEvent(change => touched.Add(change.Path));

        var refusal = await Assert.That(async () => _ = await scenario.CreateWriter().WriteAsync(request, CancellationToken.None))
            .Throws<OutputRefusalException>();

        await Assert.That(refusal!.Message).IsEqualTo($"Path '{(project ? request.ProjectPath : request.OutputRoot)}' is outside the repository.");
        await Assert.That(touched).IsEmpty();
    }

    [Test]
    [Arguments(0)]
    [Arguments(2)]
    public async Task WriteAsync_Should_Leave_Siblings_Untouched_And_Report_Marked_Leftovers(int count)
    {
        var fs = GenerationTestData.CreateFileSystem();
        var scenario = new VerifyMirrorScenario(fs);
        var parent = fs.Path.Combine(fs.Path.GetTempPath(), "opencode-sdk-generate-verify");
        var names = ((string[])["old", "older"]).Take(count).Prepend("foreign");
        foreach (var name in names)
        {
            var directory = fs.Path.Combine(parent, name);
            fs.Directory.CreateDirectory(directory);
            if (name != "foreign")
            {
                await fs.File.WriteAllTextAsync(fs.Path.Combine(directory, ".opencode-generate-verify"),
                    "opencode-sdk-generate-verify-v1\n");
            }
        }
        var before = await scenario.ReadTreeAsync(parent);
        using var console = new TestConsole();
        console.Profile.Width = 4096;
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(new SpectreConsoleLoggerProvider(console, new ToolLoggingOptions())));

        _ = await scenario.CreateWriter(logger: factory.CreateLogger<GenerationMirror>()).WriteAsync(Request([], verify: true), CancellationToken.None);

        await Assert.That(console.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r'))).IsEquivalentTo(count == 0 ? (string[])[] :
            [$"Warning: 2 marked directories under '{parent}'; they may belong to a verify that is still running or was interrupted; remove them by hand when no verify is running"], CollectionOrdering.Matching);
        await scenario.AssertTreeUnchangedAsync(before, parent);
        await Assert.That(fs.Directory.EnumerateDirectories(parent).Select(fs.Path.GetFileName).Order(StringComparer.Ordinal))
            .IsEquivalentTo(names.Cast<string?>(), CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(SimulationMode.Linux)]
    [Arguments(SimulationMode.Windows)]
    public async Task WriteAsync_Should_Mirror_Tracked_Root_Files_And_Working_Project_Without_Build_Outputs(SimulationMode mode)
    {
        var fs = new MockFileSystem(o => o.SimulatingOperatingSystem(mode));
        var scenario = new VerifyMirrorScenario(fs);
        fs.Directory.CreateDirectory("repository");
        fs.Directory.SetCurrentDirectory("repository");
        fs.Initialize().With(
            new FileDescription(".editorconfig", "root config"), new FileDescription("future-config.txt", "future config"),
            new FileDescription("untracked.txt", "untracked"), new FileDescription("nested/ignored.txt", "nested"),
            new FileDescription(GenerationTestData.ProjectPath, "working project"),
            new FileDescription(scenario.OwnedPath("Manual.cs"), "working manual"),
            new FileDescription(scenario.OwnedPath("bin/ignored.dll"), "binary"),
            new FileDescription(scenario.OwnedPath("Internal/obj/x.cs"), "nested"),
            new FileDescription(scenario.OwnedPath("Bin/mixed.dll"), "mixed case"),
            new FileDescription(scenario.OwnedPath("OBJ/upper.json"), "upper case"),
            new FileDescription(scenario.OwnedPath("obj/project.assets.json"), "assets"));
        var before = await scenario.ReadTreeAsync();
        var runner = GenerationTestData.MirrorProcesses(fs, trackedFiles: "future-config.txt\0nested/ignored.txt\0.editorconfig\0");
        var formatter = new RecordingProjectFormatter(fs, async (current, project, _, token) =>
        {
            var root = current.Path.GetFullPath(current.Path.Combine(current.Path.GetDirectoryName(project)!, "../.."));
            await Assert.That(current.Path.GetRelativePath(root, project).Replace('\\', '/')).IsEqualTo(GenerationTestData.ProjectPath);
            await Assert.That(current.Directory.EnumerateFiles(root).Select(current.Path.GetFileName).Order(StringComparer.Ordinal))
                .IsEquivalentTo((string?[])[".editorconfig", ".opencode-generate-verify", "future-config.txt"], CollectionOrdering.Matching);
            await Assert.That(await current.File.ReadAllTextAsync(project, token)).IsEqualTo("working project");
            await Assert.That(await current.File.ReadAllTextAsync(current.Path.Combine(root, "future-config.txt"), token)).IsEqualTo("future config");
            await Assert.That(await current.File.ReadAllTextAsync(current.Path.Combine(root, ".editorconfig"), token)).IsEqualTo("root config");
            await Assert.That(current.Directory.EnumerateFiles(current.Path.GetDirectoryName(project)!, "*", SearchOption.AllDirectories)
                .Select(path => current.Path.GetRelativePath(current.Path.GetDirectoryName(project)!, path).Replace('\\', '/')).Order(StringComparer.Ordinal))
                .IsEquivalentTo([".generated-manifest.json", "Manual.cs", "OpenCode.Sdk.csproj"], CollectionOrdering.Matching);
            await Assert.That(await current.File.ReadAllTextAsync(current.Path.Combine(root, GenerationTestData.OutputRoot, "Manual.cs"), token)).IsEqualTo("working manual");
            await Assert.That(current.Directory.EnumerateFileSystemEntries(current.Path.Combine(root, "empty-source"))).IsEmpty();
        });

        _ = await scenario.CreateWriter(formatter, runner).WriteAsync(Request([], verify: true), CancellationToken.None);

        await Assert.That(formatter.CallCount).IsEqualTo(1);
        await Assert.That(runner.Invocations).Count().IsEqualTo(2);
        await scenario.AssertReadOnlyVerifyAsync(before);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WriteAsync_Should_Refuse_A_Failed_Process_And_Remove_The_Mirror(bool git)
    {
        var fs = GenerationTestData.CreateFileSystem();
        var scenario = new VerifyMirrorScenario(fs);
        var before = await scenario.ReadTreeAsync();
        var failure = ScriptedProcessRunner.Fail(" cache missing \n") with { ExitCode = 7, StandardOutput = " NU1301 \n"u8.ToArray() };
        var runner = git
            ? new ScriptedProcessRunner().Expect("git", ["ls-files", "-z"], fs.Directory.GetCurrentDirectory(), failure)
            : GenerationTestData.MirrorProcesses(fs, restoreResult: failure);
        var formatter = new RecordingProjectFormatter(fs);

        var refusal = await RefusedVerifyAsync(scenario, formatter, runner);

        await Assert.That(refusal!.Message).IsEqualTo(git ? "'git ls-files' failed (exit 7): cache missing" :
            $"Staging project restore failed (exit 7): NU1301\ncache missing\nRestore the real project once ('dotnet restore {GenerationTestData.ProjectPath}') so the global packages folder holds every package.");
        await Assert.That(formatter.CallCount).IsEqualTo(0);
        await scenario.AssertReadOnlyVerifyAsync(before);
    }

    private static async Task<OutputRefusalException?> RefusedVerifyAsync(VerifyMirrorScenario scenario, IProjectFormatter formatter, IProcessRunner runner) =>
        await Assert.That(async () => _ = await scenario.CreateWriter(formatter, runner).WriteAsync(
            Request([], verify: true), CancellationToken.None)).Throws<OutputRefusalException>();
}
