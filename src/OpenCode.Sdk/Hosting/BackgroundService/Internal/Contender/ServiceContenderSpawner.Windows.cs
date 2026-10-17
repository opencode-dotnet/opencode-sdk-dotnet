using System.ComponentModel;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// The Windows arm: the SDK's Windows spawn with the placement libuv's <c>uv_spawn</c> gives a child
/// Node spawns <c>detached</c> and <c>windowsHide</c> with no standard stream inherited
/// (<c>DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_NO_WINDOW</c>, <c>SW_HIDE</c>): NUL stdin
/// and stdout, and the stderr pipe, whose child end is inheritable only while the contender is
/// created, under the lock every SDK spawn takes. A batch shim is the exception: its contender is
/// the program <c>cmd.exe</c> starts, and a detached <c>cmd.exe</c> has no console to hand it, so
/// that program would take a new console, with a visible window and its own standard streams in
/// place of the pipe. The shim's <c>cmd.exe</c> therefore gets a console without a window
/// (<c>CREATE_NO_WINDOW</c>), which the program inherits and shares with no other process.
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
                Detached = !startInfo.Executable.IsBatchScript,
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

        return new ServiceContender(child.ProcessId, stderr, child.Process, redaction, _time);
    }
}
