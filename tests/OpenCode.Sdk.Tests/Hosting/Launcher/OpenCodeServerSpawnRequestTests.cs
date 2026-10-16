using System.ComponentModel;
using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Hosting.Launcher;

/// <summary>The standalone door's pre-spawn refusals and the request it hands to the host's strategy.</summary>
public sealed class OpenCodeServerSpawnRequestTests
{
    private static string Command => OperatingSystem.IsWindows() ? @"C:\oc\opencode.exe" : "/opt/oc/opencode";

    /// <summary>A rooted executable and a leading argument are both checked before spawn, without pinning #401's parameter name.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task StartAsync_Should_Refuse_A_Nul_In_A_Command_Entry_Before_Spawn(bool executable)
    {
        var spawn = new RecordingSpawn();
        var options = new OpenCodeServerOptions { Command = executable ? [Command + "\0", "serve"] : [Command, "serve\0"] };

        var exception = await Assert.That(async () => await OpenCodeServer.StartWithSeamsAsync(options, Seams(spawn), CancellationToken.None))
            .Throws<ArgumentException>();

        await Assert.That(exception!.Message).IsEqualTo(new ArgumentException(
            "OpenCodeServerOptions.Command entries cannot contain NUL.", exception.ParamName).Message);
        await Assert.That(spawn.Calls).IsEqualTo(0);
    }

    /// <summary>Neither an environment name nor its value may be silently cut at a NUL by native marshalling.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task StartAsync_Should_Refuse_A_Nul_In_An_Environment_Entry_Before_Spawn(bool name)
    {
        var spawn = new RecordingSpawn();
        var options = new OpenCodeServerOptions
        {
            Command = [Command],
            Environment = new Dictionary<string, string>(StringComparer.Ordinal) { [name ? "ENTRY\0" : "ENTRY"] = name ? "value" : "value\0" },
        };
        var seams = Seams(spawn);

        var exception = await Assert.That(async () => await OpenCodeServer.StartWithSeamsAsync(options, seams, cancellationToken: CancellationToken.None))
            .Throws<ArgumentException>();

        await Assert.That(exception!.Message).IsEqualTo(new ArgumentException(
            "OpenCodeServerOptions.Environment names and values cannot contain NUL.", exception.ParamName).Message);
        await Assert.That(spawn.Calls).IsEqualTo(0);
    }

    /// <summary>The launcher tail follows every supplied argument; Windows starts that exact command suspended.</summary>
    [Test]
    public async Task StartAsync_Should_Append_The_Standalone_Arguments_After_The_Supplied_Arguments()
    {
        var spawn = new RecordingSpawn();
        var options = new OpenCodeServerOptions { Command = [Command, "serve", "two words", "quoted\"argument"] };

        _ = await Assert.That(async () => await OpenCodeServer.StartWithSeamsAsync(options, Seams(spawn), CancellationToken.None))
            .Throws<OpenCodeServerException>();

        string[] argv = [Command, "serve", "two words", "quoted\"argument", "--stdio", "--port", "0"];
        if (OperatingSystem.IsWindows())
        {
            await Assert.That(spawn.WindowsRequest!.ApplicationPath).IsEqualTo(Command);
            await Assert.That(spawn.WindowsRequest.CommandLine).IsEqualTo(
                """C:\oc\opencode.exe serve "two words" "quoted\"argument" --stdio --port 0""");
            await Assert.That(spawn.WindowsRequest.StartSuspended).IsTrue();
        }
        else
        {
            await Assert.That(new[] { spawn.PosixRequest!.ExecutablePath }.Concat(spawn.PosixRequest.Arguments))
                .IsEquivalentTo(argv, CollectionOrdering.Matching);
        }
    }

    /// <summary>Each start injects a fresh base64url lease credential, never the caller's replacement.</summary>
    [Test]
    public async Task StartAsync_Should_Inject_A_Fresh_Base64Url_Password_Over_The_Callers_Entry()
    {
        var spawn = new RecordingSpawn();
        var seams = Seams(spawn);
        const string callerPassword = "caller-password";
        var options = new OpenCodeServerOptions
        {
            Command = [Command],
            Environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["OPENCODE_PASSWORD"] = callerPassword },
        };

        _ = await Assert.That(async () => await OpenCodeServer.StartWithSeamsAsync(options, seams, CancellationToken.None))
            .Throws<OpenCodeServerException>();
        var first = spawn.Environment["OPENCODE_PASSWORD"];
        _ = await Assert.That(async () => await OpenCodeServer.StartWithSeamsAsync(options, seams, CancellationToken.None))
            .Throws<OpenCodeServerException>();
        var second = spawn.Environment["OPENCODE_PASSWORD"];

        await Assert.That(first).Matches("^[A-Za-z0-9_-]{43}$");
        await Assert.That(second).Matches("^[A-Za-z0-9_-]{43}$");
        await Assert.That(second).IsNotEqualTo(first);
        await Assert.That(first).IsNotEqualTo(callerPassword);
        await Assert.That(second).IsNotEqualTo(callerPassword);
    }

    /// <summary>Only the host's literal 1 inherits stderr; the ownership lease and readiness stream remain pipes.</summary>
    [Test]
    [Arguments("1", true)]
    [Arguments("true", false)]
    public async Task StartAsync_Should_Inherit_Stderr_Only_For_The_Hosts_Print_Logs_Switch(string printLogs, bool inherit)
    {
        var spawn = new RecordingSpawn();
        var host = new Dictionary<string, string>(StringComparer.Ordinal) { ["OPENCODE_PRINT_LOGS"] = printLogs };
        var options = new OpenCodeServerOptions { Command = [Command] };
        var expected = inherit ? ChildStreamRoute.Inherit : ChildStreamRoute.Pipe;

        _ = await Assert.That(async () => await OpenCodeServer.StartWithSeamsAsync(options, Seams(spawn, host), CancellationToken.None))
            .Throws<OpenCodeServerException>();

        if (OperatingSystem.IsWindows())
        {
            await Assert.That(spawn.WindowsRequest!.StandardStreams!.Input).IsEqualTo(ChildStreamRoute.Pipe);
            await Assert.That(spawn.WindowsRequest.StandardStreams.Output).IsEqualTo(ChildStreamRoute.Pipe);
            await Assert.That(spawn.WindowsRequest.StandardStreams.Error).IsEqualTo(expected);
        }
        else
        {
            await Assert.That(spawn.PosixRequest!.StandardInput).IsEqualTo(ChildStreamRoute.Pipe);
            await Assert.That(spawn.PosixRequest.StandardOutput).IsEqualTo(ChildStreamRoute.Pipe);
            await Assert.That(spawn.PosixRequest.StandardError).IsEqualTo(expected);
        }
    }

    /// <summary>A spawn failure names a meaningful directory, while a blank directory is passed as null and omitted.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task StartAsync_Should_Name_The_Working_Directory_Only_When_It_Is_Not_Blank(bool directory)
    {
        var spawn = new RecordingSpawn();
        var work = OperatingSystem.IsWindows() ? @"C:\work" : "/work";
        var options = new OpenCodeServerOptions { Command = [Command], WorkingDirectory = directory ? work : " " };

        var exception = await Assert.That(async () => await OpenCodeServer.StartWithSeamsAsync(options, Seams(spawn), CancellationToken.None))
            .Throws<OpenCodeServerException>();

        await Assert.That(exception!.Message).IsEqualTo(directory
            ? $"Failed to start the server command '{Command}' in the working directory '{work}'."
            : $"Failed to start the server command '{Command}'.");
        await Assert.That(OperatingSystem.IsWindows() ? spawn.WindowsRequest!.WorkingDirectory : spawn.PosixRequest!.WorkingDirectory)
            .IsEqualTo(directory ? work : null);
    }

    private static LauncherSeams Seams(RecordingSpawn spawn, IReadOnlyDictionary<string, string>? host = null) => new()
    {
        WindowsSpawn = spawn,
        Spawn = spawn,
        Job = Substitute.For<IProcessJob>(),
        WindowsExits = Substitute.For<IWindowsChildExit>(),
        TreeKill = Substitute.For<IWindowsTreeKill>(),
        Signals = Substitute.For<IProcessGroupSignal>(),
        Exits = Substitute.For<IChildExitStatus>(),
        Time = new ManualTimeProvider(),
        HostEnvironment = host ?? new Dictionary<string, string>(StringComparer.Ordinal),
    };

    /// <summary>Records the host strategy's request and refuses to create a process.</summary>
    private sealed class RecordingSpawn : IPosixSpawn, IWindowsSpawn
    {
        public int Calls { get; private set; }

        public PosixSpawnRequest? PosixRequest { get; private set; }

        public WindowsSpawnRequest? WindowsRequest { get; private set; }

        public IReadOnlyDictionary<string, string> Environment => WindowsRequest?.Environment ?? PosixRequest!.Environment;

        public PosixSpawnedChild Spawn(PosixSpawnRequest request)
        {
            Calls++;
            PosixRequest = request;
            throw new Win32Exception(2);
        }

        public WindowsSpawnedChild Spawn(WindowsSpawnRequest request)
        {
            Calls++;
            WindowsRequest = request;
            throw new Win32Exception(2);
        }
    }
}
