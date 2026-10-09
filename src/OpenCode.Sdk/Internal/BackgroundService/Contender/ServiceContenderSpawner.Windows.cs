using System.ComponentModel;
using OpenCode.Sdk.Internal.BackgroundService.Abstractions;
using OpenCode.Sdk.Internal.Posix;
using OpenCode.Sdk.Internal.Windows;

namespace OpenCode.Sdk.Internal.BackgroundService.Contender;

/// <summary>
/// The Windows arm: the SDK's Windows spawn with the placement libuv's <c>uv_spawn</c> gives a child
/// Node spawns <c>detached</c> and <c>windowsHide</c> with no standard stream inherited
/// (<c>DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_NO_WINDOW</c>, <c>SW_HIDE</c>): NUL stdin
/// and stdout, and the stderr pipe, whose child end is inheritable only while the contender is
/// created, under the lock every SDK spawn takes.
/// </summary>
internal sealed partial class ServiceContenderSpawner
{
    private ServiceContender SpawnWindows(
        IServiceContenderSpawner.ContenderStartInfo startInfo,
        Dictionary<string, string> environment,
        string? redaction)
    {
        var command = WindowsCommandLine.For(startInfo.Executable, startInfo.Arguments, launcherArguments: []);
        WindowsSpawnedChild child;
        try
        {
            child = _windowsSpawn.Spawn(new WindowsSpawnRequest
            {
                ApplicationPath = command.ApplicationPath,
                CommandLine = command.CommandLine,
                Environment = environment,
                StandardStreams = new WindowsStandardStreams
                {
                    Input = ChildStreamRoute.Null,
                    Output = ChildStreamRoute.Null,
                    Error = ChildStreamRoute.Pipe,
                },
                Detached = true,
            });
        }
        catch (Win32Exception failure)
        {
            throw SpawnFailure(startInfo, failure);
        }

        var stderr = child.StandardError;
        if (stderr is null)
        {
            child.Process.Dispose();
            throw new InvalidOperationException("The contender's standard error was routed to a pipe, but the spawn returned no read end.");
        }

        return new ServiceContender(child.ProcessId, stderr, child.Process, redaction);
    }
}
