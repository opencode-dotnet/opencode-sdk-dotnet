using NSubstitute;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>Shares substituted launcher seams and drives an owned child through the POSIX strategy on every host.</summary>
internal sealed class LauncherChildScenario : IAsyncDisposable
{
    public LauncherChildScenario()
    {
        Seams = CreateSeams() with
        {
            Exits = Exits,
            Time = new ManualTimeProvider(),
        };
        Seams.Signals.ProbeGroup(Arg.Any<int>()).Returns(SignalDelivery.NoSuchTarget);
        Seams.Spawn.Spawn(Arg.Any<PosixSpawnRequest>()).Returns(call =>
        {
            Request = call.Arg<PosixSpawnRequest>();
            return new PosixSpawnedChild
            {
                ProcessId = 12345,
                StandardInput = Lease,
                StandardOutput = new MemoryStream(),
                StandardError = new MemoryStream(),
            };
        });
    }

    internal static LauncherSeams CreateSeams(IWindowsSpawn? windowsSpawn = null, IWindowsChildExit? windowsExits = null) => new()
    {
        WindowsSpawn = windowsSpawn ?? Substitute.For<IWindowsSpawn>(),
        Job = Substitute.For<IProcessJob>(),
        WindowsExits = windowsExits ?? Substitute.For<IWindowsChildExit>(),
        TreeKill = Substitute.For<IWindowsTreeKill>(),
        Spawn = Substitute.For<IPosixSpawn>(),
        Signals = Substitute.For<IProcessGroupSignal>(),
        Exits = Substitute.For<IChildExitStatus>(),
        Time = TimeProvider.System,
        HostEnvironment = new Dictionary<string, string>(StringComparer.Ordinal),
    };

    public LauncherSeams Seams { get; }
    public ScriptedChildExit Exits { get; } = new();
    public MemoryStream Lease { get; } = new();
    public PosixSpawnRequest? Request { get; private set; }
    public ServerChildStart? Start { get; private set; }
    public OwnedServerChild? Child { get; private set; }

    public async Task<OwnedServerChild> LaunchAsync(ServerChildStart start, LauncherSeams seams)
    {
        Start = start;
        Child = await PosixServerChild.SpawnAsync(start, seams);
        return Child;
    }

    public async ValueTask DisposeAsync()
    {
        Exits.Exit(new ChildExitStatus { ExitCode = 0 });
        if (Child is not null)
        {
            await Child.DisposeAsync();
        }
        Exits.Dispose();
        await Lease.DisposeAsync();
    }
}
