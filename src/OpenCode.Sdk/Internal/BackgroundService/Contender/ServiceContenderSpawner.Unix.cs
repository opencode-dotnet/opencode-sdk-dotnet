using System.ComponentModel;
using OpenCode.Sdk.Internal.BackgroundService.Abstractions;
using OpenCode.Sdk.Internal.Posix;

namespace OpenCode.Sdk.Internal.BackgroundService.Contender;

/// <summary>The Unix arm: the shared POSIX spawn, in a new session with default signal state, <c>/dev/null</c> on 0 and 1, and the stderr pipe on 2.</summary>
internal sealed partial class ServiceContenderSpawner
{
    private ServiceContender SpawnUnix(
        IServiceContenderSpawner.ContenderStartInfo startInfo,
        Dictionary<string, string> environment,
        string? redaction)
    {
        PosixSpawnedChild child;
        try
        {
            child = _posixSpawn.Spawn(new PosixSpawnRequest
            {
                ExecutablePath = startInfo.Executable.Path,
                Arguments = startInfo.Arguments,
                Environment = environment,
                StandardInput = ChildStreamRoute.Null,
                StandardOutput = ChildStreamRoute.Null,
                StandardError = ChildStreamRoute.Pipe,
            });
        }
        catch (PlatformNotSupportedException exception)
        {
            // Without a session the contender would stay in the launcher's group, which the detach
            // contract forbids, so the refusal is the contender's own failure, not a spawn's.
            throw new OpenCodeServerException(
                "The background service contender needs a detached session the platform does not provide: no contender was started.",
                exception);
        }
        catch (Win32Exception exception)
        {
            throw SpawnFailure(startInfo, exception);
        }

        // The request routes standard error to a pipe, so the spawn always returns its read end, and
        // the contender owns it from here.
        var stderr = child.StandardError
                     ?? throw new InvalidOperationException("The contender's standard error was routed to a pipe, but the spawn returned no read end.");
        return new ServiceContender(child.ProcessId, stderr, null, redaction);
    }
}
