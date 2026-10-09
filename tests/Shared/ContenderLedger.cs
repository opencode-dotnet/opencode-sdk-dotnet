using System.Globalization;
using System.IO.Abstractions;
using System.Text;
using OpenCode.Sdk.Internal.BackgroundService.Abstractions;
using OpenCode.Sdk.Internal.BackgroundService.Contender;
using OpenCode.Sdk.Internal.Posix;
using OpenCode.Sdk.Internal.Windows;

namespace OpenCode.Sdk.TestSupport;

/// <summary>
/// Every process the Ensure elections of one test started, learned where the election starts
/// them: a spawner that wraps the platform spawn and appends each contender's
/// <see cref="ProcessMark"/> to a ledger file before handing the contender back. The door releases
/// its contenders rather than returning them, the way the pinned loop does, so this is the one
/// place a test can learn them; the file lets the isolated fixture process record into the same
/// ledger its launching test reads.
/// </summary>
/// <param name="fileSystem">The filesystem the ledger and <c>/proc</c> are read through.</param>
/// <param name="path">The ledger file.</param>
internal sealed class ContenderLedger(IFileSystem fileSystem, string path) : IServiceContenderSpawner
{
    private readonly ServiceContenderSpawner _spawner = new(new PosixSpawn(), new WindowsSpawn());
    private readonly Lock _gate = new();

    public IServiceContender Spawn(IServiceContenderSpawner.ContenderStartInfo startInfo)
    {
        var contender = _spawner.Spawn(startInfo);

        // Marked at once: a pid read later may already name a newer process. A contender gone
        // before it could be marked has nothing left for teardown to end — on Windows the batch
        // shim's cmd.exe host lives exactly as long as the server it waits on.
        if (ProcessMark.TryRead(fileSystem, contender.ProcessId) is { } mark)
        {
            lock (_gate)
            {
                using var stream = fileSystem.FileStream.New(
                    path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                stream.Write(Encoding.UTF8.GetBytes(mark + "\n"));
            }
        }

        return contender;
    }

    /// <summary>Reads every recorded contender that still runs; a ledger nothing wrote to is empty.</summary>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The live contenders, in spawn order.</returns>
    /// <exception cref="InvalidOperationException">A line is not one recorded mark.</exception>
    public async Task<IReadOnlyList<ProcessMark>> ReadLiveAsync(CancellationToken cancellationToken)
    {
        if (!fileSystem.File.Exists(path))
        {
            return [];
        }

        string text;
        using (var stream = fileSystem.FileStream.New(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        return [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Parse).Where(mark => mark.IsRunning(fileSystem))];
    }

    private ProcessMark Parse(string line) =>
        line.Split(' ') is [var pid, var start]
        && int.TryParse(pid, NumberStyles.None, CultureInfo.InvariantCulture, out var processId)
        && long.TryParse(start, NumberStyles.None, CultureInfo.InvariantCulture, out var startMarker)
            ? new ProcessMark(processId, startMarker)
            : throw new InvalidOperationException($"The contender ledger '{path}' holds a line that is not one process mark: '{line}'.");
}
