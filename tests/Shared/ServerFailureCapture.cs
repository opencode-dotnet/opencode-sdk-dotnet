using System.Globalization;
using System.IO.Abstractions;
using System.Text;
using OpenCode.Sdk.TestSupport.Ownership;
using OpenCode.Sdk.TestSupport.Ownership.Abstractions;

namespace OpenCode.Sdk.TestSupport;

/// <summary>Captures output and then failure metadata under independent finite deadlines.</summary>
internal sealed class ServerFailureCapture(
    ServerFailureArtifacts artifacts, IFileSystem fileSystem, IOwnedOperationDeadline deadline)
{
    private const string TruncatedHeader = "[truncated: older lines, or part of an oversized line, were discarded to stay inside the retention bounds]";

    /// <summary>The folder beside the failure metadata that holds the server's own log files.</summary>
    private const string ServerLogFolder = "server-logs";

    /// <summary>How many of the server's log files one failure keeps; a run writes one or two.</summary>
    private const int MaxServerLogFiles = 8;

    /// <summary>How much of each server log one failure keeps: its newest bytes.</summary>
    private const int MaxServerLogBytes = 1024 * 1024;

    private readonly List<LateCleanupFailureReport> _lateReports = [];

    public IReadOnlyList<LateCleanupFailureReport> LateReports => _lateReports;

    /// <summary>
    /// Writes the collector's final snapshot (when a server was owned), the server's own log files
    /// (when a folder is named), and the failure metadata. The snapshot is taken here, after the
    /// owned server's disposal has settled, so it is the launcher's final collection rather than a
    /// mid-shutdown view.
    /// </summary>
    /// <param name="serverLogDirectory">
    /// The folder the server writes its own log files to, or null. Only its <c>*.log</c> files are
    /// kept, never the data root around them, which holds the server's database and registrations.
    /// </param>
    public async Task<Exception?> CaptureAsync(OpenCodeServerOutput? output, int? processId, bool external,
        Exception? failure, IReadOnlyList<Exception> teardownFailures, string? serverLogDirectory = null)
    {
        var capture = new OwnedCleanup(TimeSpan.FromSeconds(5), deadline);
        if (output is not null)
        {
            var snapshot = output.GetSnapshot();
            capture.Own("pinned server stdout capture",
                _ => WriteLogAsync("stdout.log", snapshot.StandardOutput, snapshot.StandardOutputTruncated));
            capture.Own("pinned server stderr capture",
                _ => WriteLogAsync("stderr.log", snapshot.StandardError, snapshot.StandardErrorTruncated));
        }

        if (serverLogDirectory is not null)
        {
            capture.Own("pinned server log files capture", _ => CopyServerLogsAsync(serverLogDirectory));
        }

        failure = await CaptureFailureAsync(capture, failure);
        if (failure is not null && artifacts.Failures.Count is 0)
        {
            _ = artifacts.Mark(failure, "fixture disposal", "phase=server diagnostic capture");
        }

        var lifecycleFailures = teardownFailures.Concat(capture.OperationFailures).ToArray();
        var metadata = new OwnedCleanup(TimeSpan.FromSeconds(5), deadline);
        metadata.Own("pinned server failure metadata", _ =>
            artifacts.WriteMetadataAsync(external ? "external" : "owned",
                processId?.ToString(CultureInfo.InvariantCulture) ?? "not-owned", lifecycleFailures));
        return await CaptureFailureAsync(metadata, failure);
    }

    private async Task WriteLogAsync(string name, IReadOnlyList<string> lines, bool truncated)
    {
        _ = fileSystem.Directory.CreateDirectory(artifacts.Directory);
        var content = truncated ? lines.Prepend(TruncatedHeader) : lines;
        await new DiagnosticFileWriter(fileSystem).WriteAsync(
            fileSystem.Path.Combine(artifacts.Directory, name), string.Join(Environment.NewLine, content));
    }

    /// <summary>
    /// Keeps the newest part of each of the server's log files: the server logs what a stalled
    /// request was waiting on (the processes it spawned, when a location finished building), which
    /// neither its stdout nor its stderr carries.
    /// </summary>
    private async Task CopyServerLogsAsync(string source)
    {
        if (!fileSystem.Directory.Exists(source))
        {
            return;
        }

        var target = fileSystem.Path.Combine(artifacts.Directory, ServerLogFolder);
        foreach (var file in fileSystem.Directory.EnumerateFiles(source, "*.log", SearchOption.TopDirectoryOnly).Take(MaxServerLogFiles))
        {
            _ = fileSystem.Directory.CreateDirectory(target);
            var stream = fileSystem.File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            string text;
            await using (stream.ConfigureAwait(false))
            {
                var skipped = Math.Max(0, stream.Length - MaxServerLogBytes);
                _ = stream.Seek(skipped, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                text = await reader.ReadToEndAsync().ConfigureAwait(false);
                if (skipped > 0)
                {
                    text = TruncatedHeader + Environment.NewLine + text;
                }
            }

            await new DiagnosticFileWriter(fileSystem).WriteAsync(
                fileSystem.Path.Combine(target, fileSystem.Path.GetFileName(file)), text).ConfigureAwait(false);
        }
    }

    private async Task<Exception?> CaptureFailureAsync(OwnedCleanup cleanup, Exception? primary)
    {
        try
        {
            await cleanup.CompleteAsync(primary);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
        finally
        {
            if (cleanup.LateFailures is { } late)
            {
                _lateReports.Add(late);
            }
        }
    }
}
