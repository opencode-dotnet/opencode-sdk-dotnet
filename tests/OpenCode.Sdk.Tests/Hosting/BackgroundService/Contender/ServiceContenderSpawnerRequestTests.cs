using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using NSubstitute;
using NSubstitute.Extensions;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Contender;

/// <summary>
/// The contender spawner's pre-spawn refusals, failure text and platform request: no process is
/// started. Each platform records its own request through substitute spawn seams; environment
/// assertions read only that request, not how many snapshots or merges produced it.
/// </summary>
public sealed class ServiceContenderSpawnerRequestTests
{
    private const string CommandPath = "/tools/opencode";
    private const string HandoffVariable = "OPENCODE_PTY_HANDOFF";

    [Test]
    [Arguments("bad\0key", "value")]
    [Arguments("key", "bad\0value")]
    public async Task Spawn_Should_Refuse_Nul_In_An_Overlay_Key_Or_Value(string key, string value)
    {
        var recording = new RecordingSpawn();
        var start = Start(environment: new Dictionary<string, string?>(StringComparer.Ordinal) { [key] = value });

        var exception = await Assert.That(() => Spawner(recording).Spawn(start)).Throws<ArgumentException>();

        await Assert.That(exception!.Message).IsEqualTo(new ArgumentException(
            "ContenderStartInfo.Environment keys and values cannot contain NUL.", exception.ParamName).Message);
        _ = recording.Posix.DidNotReceive().Spawn(Arg.Any<PosixSpawnRequest>());
        _ = recording.Windows.DidNotReceive().Spawn(Arg.Any<WindowsSpawnRequest>());
    }

    [Test]
    public async Task Spawn_Should_Route_A_Batch_Shim_Through_Cmd_Only_On_Windows()
    {
        var recording = new RecordingSpawn();
        var start = Start(executable: new ResolvedExecutable("/tools/opencode.cmd", "/tools/opencode.cmd", IsBatchScript: true));

        var exception = await Assert.That(() => Spawner(recording).Spawn(start)).Throws<OpenCodeServerException>();

        if (OperatingSystem.IsWindows())
        {
            var request = recording.WindowsRequest!;
            await Assert.That(request.ApplicationPath).IsEqualTo(BatchCommandLine.InterpreterPath);
            await Assert.That(request.Detached).IsFalse();
            BranchReport.Print("Windows — batch shim uses cmd.exe without detaching");
        }
        else
        {
            await Assert.That(exception!.Message).IsEqualTo(
                "The background service contender routes through cmd.exe, which exists only on Windows: no contender was started.");
            _ = recording.Posix.DidNotReceive().Spawn(Arg.Any<PosixSpawnRequest>());
            _ = recording.Windows.DidNotReceive().Spawn(Arg.Any<WindowsSpawnRequest>());
            BranchReport.Print("POSIX — batch shim refused before spawning");
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Spawn_Should_Name_The_Command_With_Any_Resolution_Inside_The_Quotes(bool resolved)
    {
        var recording = new RecordingSpawn();
        var start = Start(executable: new ResolvedExecutable(resolved ? "oc" : CommandPath, CommandPath, IsBatchScript: false));

        var exception = await Assert.That(() => Spawner(recording).Spawn(start)).Throws<OpenCodeServerException>();

        await Assert.That(exception!.Message).IsEqualTo(resolved
            ? "Failed to start the background service contender 'oc (resolved to '/tools/opencode')'."
            : "Failed to start the background service contender '/tools/opencode'.");
        await Assert.That(exception.InnerException).IsSameReferenceAs(recording.Failure);
    }

    [Test]
    public async Task Spawn_Should_Request_Null_Input_And_Output_Piped_Error_And_An_Overlaid_Environment()
    {
        const string setVariable = "SDK_CONTENDER_REQUEST_VALUE";
        const string setValue = "overlay value";
        await Assert.That(Environment.GetEnvironmentVariable("PATH")).IsNotNull();
        var recording = new RecordingSpawn();
        var start = Start(environment: new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PATH"] = null,
            [setVariable] = setValue,
        });

        await Assert.That(() => Spawner(recording).Spawn(start)).Throws<OpenCodeServerException>();

        await Assert.That(recording.Environment).DoesNotContainKey("PATH");
        await Assert.That(recording.Environment[setVariable]).IsEqualTo(setValue);

        if (OperatingSystem.IsWindows())
        {
            var request = recording.WindowsRequest!;
            await Assert.That(request.StandardStreams!.Input).IsEqualTo(ChildStreamRoute.Null);
            await Assert.That(request.StandardStreams.Output).IsEqualTo(ChildStreamRoute.Null);
            await Assert.That(request.StandardStreams.Error).IsEqualTo(ChildStreamRoute.Pipe);
            await Assert.That(request.Detached).IsTrue();
            BranchReport.Print("Windows — non-batch contender detached with NUL/NUL/pipe");
        }
        else
        {
            var request = recording.PosixRequest!;
            await Assert.That(request.StandardInput).IsEqualTo(ChildStreamRoute.Null);
            await Assert.That(request.StandardOutput).IsEqualTo(ChildStreamRoute.Null);
            await Assert.That(request.StandardError).IsEqualTo(ChildStreamRoute.Pipe);
            BranchReport.Print("POSIX — contender request uses null/null/pipe");
        }
    }

    [Test]
    [Arguments("OCSDK_PROBE", new[] { "ocsdk_probe" }, new[] { "S" }, new[] { "OCSDK_PROBE" }, new[] { "H" }, new[] { "OCSDK_PROBE", "ocsdk_probe" }, new[] { "H", "S" })]
    [Arguments("ocsdk_probe", new[] { "OCSDK_PROBE" }, new[] { "S" }, new[] { "OCSDK_PROBE" }, new[] { "S" }, new[] { "OCSDK_PROBE", "ocsdk_probe" }, new[] { "S", "H" })]
    [Arguments("Ocsdk_Probe", new[] { "OCSDK_PROBE" }, new[] { "S" }, new[] { "OCSDK_PROBE" }, new[] { "S" }, new[] { "OCSDK_PROBE", "Ocsdk_Probe" }, new[] { "S", "H" })]
    [Arguments("OCSDK_PROBE", new[] { "Ocsdk_Probe" }, new[] { "S" }, new[] { "OCSDK_PROBE" }, new[] { "H" }, new[] { "OCSDK_PROBE", "Ocsdk_Probe" }, new[] { "H", "S" })]
    [Arguments("Ocsdk_Probe", new[] { "ocsdk_probe" }, new[] { "S" }, new[] { "Ocsdk_Probe" }, new[] { "H" }, new[] { "Ocsdk_Probe", "ocsdk_probe" }, new[] { "H", "S" })]
    [Arguments("OCSDK_PROBE", new[] { "OCSDK_PROBE" }, new[] { "S" }, new[] { "OCSDK_PROBE" }, new[] { "S" }, new[] { "OCSDK_PROBE" }, new[] { "S" })]
    [Arguments(null, new[] { "ocsdk_probe", "OCSDK_PROBE" }, new[] { "S", "T" }, new[] { "OCSDK_PROBE" }, new[] { "T" }, new[] { "OCSDK_PROBE", "ocsdk_probe" }, new[] { "T", "S" })]
    [Arguments(null, new[] { "Ocsdk_Probe", "oCSDK_probe" }, new[] { "T", "S" }, new[] { "Ocsdk_Probe" }, new[] { "T" }, new[] { "Ocsdk_Probe", "oCSDK_probe" }, new[] { "T", "S" })]
    [Arguments("OCSDK_PROBE", new[] { "OCSDK_PROBE", "ocsdk_probe" }, new[] { "S", "T" }, new[] { "OCSDK_PROBE" }, new[] { "S" }, new[] { "OCSDK_PROBE", "ocsdk_probe" }, new[] { "S", "T" })]
    [Arguments("OCSDK_PROBE", new[] { "ocsdk_probe" }, new string?[] { null }, new[] { "OCSDK_PROBE" }, new[] { "H" }, new[] { "OCSDK_PROBE" }, new[] { "H" })]
    [Arguments("OCSDK_PROBE", new[] { "OCSDK_PROBE" }, new string?[] { null }, new string[0], new string[0], new string[0], new string[0])]
    [Arguments("ocsdk_probe", new[] { "OCSDK_PROBE" }, new string?[] { null }, new string[0], new string[0], new[] { "ocsdk_probe" }, new[] { "H" })]
    [Arguments("OCSDK_PROBE", new[] { "ocsdk_probe" }, new[] { "" }, new[] { "OCSDK_PROBE" }, new[] { "H" }, new[] { "OCSDK_PROBE", "ocsdk_probe" }, new[] { "H", "" })]
    [Arguments("ocsdk_probe", new[] { "OCSDK_PROBE" }, new[] { "" }, new[] { "OCSDK_PROBE" }, new[] { "" }, new[] { "OCSDK_PROBE", "ocsdk_probe" }, new[] { "", "H" })]
    public async Task Spawn_Should_Resolve_Environment_Spellings_As_The_Pinned_Client_Does(
        string? hostName, string[] overlayNames, string?[] overlayValues,
        string[] windowsNames, string[] windowsValues, string[] unixNames, string[] unixValues)
    {
        ArgumentNullException.ThrowIfNull(overlayNames);
        ArgumentNullException.ThrowIfNull(overlayValues);
        var suffix = "_" + Guid.NewGuid().ToString("N");
        var overlay = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var index = 0; index < overlayNames.Length; index++)
        {
            overlay.Add(overlayNames[index] + suffix, overlayValues[index]);
        }

        try
        {
            if (hostName is not null)
            {
                Environment.SetEnvironmentVariable(hostName + suffix, "H");
            }

            var recording = new RecordingSpawn();
            await Assert.That(() => Spawner(recording).Spawn(Start(environment: overlay))).Throws<OpenCodeServerException>();

            var entries = recording.Environment.Where(entry => entry.Key.EndsWith(suffix, StringComparison.Ordinal))
                .OrderBy(static entry => entry.Key, StringComparer.Ordinal).ToArray();
            var expectedNames = OperatingSystem.IsWindows() ? windowsNames : unixNames;
            var expectedValues = OperatingSystem.IsWindows() ? windowsValues : unixValues;
            await Assert.That(entries.Select(static entry => entry.Key)).IsEquivalentTo(
                expectedNames.Select(name => name + suffix), StringComparer.Ordinal, CollectionOrdering.Matching);
            await Assert.That(entries.Select(static entry => entry.Value)).IsEquivalentTo(
                expectedValues, StringComparer.Ordinal, CollectionOrdering.Matching);
            BranchReport.Print(OperatingSystem.IsWindows() ? "Windows — first ordinal spelling wins" : "POSIX — exact spellings stand independently");
        }
        finally
        {
            if (hostName is not null)
            {
                Environment.SetEnvironmentVariable(hostName + suffix, null);
            }
        }
    }

    [Test]
    [Arguments("synthetic-ticket")]
    [Arguments("")]
    public async Task Spawn_Should_Pass_A_Nonempty_Handoff_And_Remove_An_Empty_One(string ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var recording = new RecordingSpawn();
        var start = Start(environment: new Dictionary<string, string?>(StringComparer.Ordinal) { [HandoffVariable] = ticket });

        await Assert.That(() => Spawner(recording).Spawn(start)).Throws<OpenCodeServerException>();

        if (ticket.Length == 0)
        {
            await Assert.That(recording.Environment).DoesNotContainKey(HandoffVariable);
        }
        else
        {
            await Assert.That(recording.Environment[HandoffVariable]).IsEqualTo(ticket);
        }
    }

    [Test]
    public async Task Spawn_Should_Refuse_A_Missing_Stderr_Read_End_And_Close_The_Windows_Handle()
    {
        using var process = new SafeProcessHandle(IntPtr.Zero, ownsHandle: false);
        var recording = new RecordingSpawn();
        recording.Posix.Configure().Spawn(Arg.Any<PosixSpawnRequest>()).Returns(new PosixSpawnedChild { ProcessId = Environment.ProcessId });
        recording.Windows.Configure().Spawn(Arg.Any<WindowsSpawnRequest>()).Returns(new WindowsSpawnedChild
        {
            ProcessId = Environment.ProcessId,
            Process = process,
        });

        await Assert.That(() => Spawner(recording).Spawn(Start())).Throws<InvalidOperationException>();

        if (OperatingSystem.IsWindows())
        {
            await Assert.That(process.IsClosed).IsTrue();
            BranchReport.Print("Windows — missing stderr read end refused and process handle closed");
        }
        else
        {
            BranchReport.Print("POSIX — missing stderr read end refused; no process handle is returned");
        }
    }

    private static ServiceContenderSpawner Spawner(RecordingSpawn recording) => new(recording.Posix, recording.Windows, TimeProvider.System);

    private static IServiceContenderSpawner.ContenderStartInfo Start(
        ResolvedExecutable? executable = null, IReadOnlyDictionary<string, string?>? environment = null) =>
        new(executable ?? new ResolvedExecutable(CommandPath, CommandPath, IsBatchScript: false), [],
            environment ?? new Dictionary<string, string?>(StringComparer.Ordinal));

    /// <summary>Records the current platform's request and throws one shared native spawn failure.</summary>
    private sealed class RecordingSpawn
    {
        public IPosixSpawn Posix { get; } = Substitute.For<IPosixSpawn>();
        public IWindowsSpawn Windows { get; } = Substitute.For<IWindowsSpawn>();
        public Win32Exception Failure { get; } = new(2);
        public PosixSpawnRequest? PosixRequest { get; private set; }
        public WindowsSpawnRequest? WindowsRequest { get; private set; }
        public IReadOnlyDictionary<string, string> Environment =>
            OperatingSystem.IsWindows() ? WindowsRequest!.Environment! : PosixRequest!.Environment;

        public RecordingSpawn()
        {
            Posix.Spawn(Arg.Any<PosixSpawnRequest>()).Returns(call =>
            {
                PosixRequest = call.Arg<PosixSpawnRequest>();
                throw Failure;
            });
            Windows.Spawn(Arg.Any<WindowsSpawnRequest>()).Returns(call =>
            {
                WindowsRequest = call.Arg<WindowsSpawnRequest>();
                throw Failure;
            });
        }
    }
}
