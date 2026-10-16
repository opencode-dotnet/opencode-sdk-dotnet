using System.Globalization;
using System.IO.Abstractions;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;

namespace OpenCode.Sdk.Tests.Harness;

/// <summary>Writes bounded diagnostic files beneath the runner's uploaded results directory.</summary>
internal sealed class ServerFailureArtifacts
{
    private readonly IFileSystem _fileSystem;
    private readonly FailureDiagnosticText _text = new();
    private readonly List<ServerTestFailure> _failures = [];
    private readonly Lock _gate = new();
    private readonly string _assembly = typeof(ServerFailureArtifacts).Assembly.GetName().Name ?? "tests";

    public ServerFailureArtifacts(IFileSystem fileSystem, string resultsDirectory)
    {
        _fileSystem = fileSystem;
        Framework = typeof(ServerFailureArtifacts).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName
                    ?? "unknown-framework";
        Directory = fileSystem.Path.Combine(resultsDirectory, "server-diagnostics",
            _assembly, Guid.NewGuid().ToString("N"));
    }

    public string Framework { get; }

    public string Directory { get; }

    public IReadOnlyList<ServerTestFailure> Failures
    {
        get
        {
            lock (_gate)
            {
                return [.. _failures];
            }
        }
    }

    public string Mark(Exception exception, string test, string details, string? invocation = null)
    {
        lock (_gate)
        {
            var identity = invocation ?? test;
            var failure = _failures.Find(item => item.Invocation == identity && ReferenceEquals(item.Exception, exception));
            if (failure is null)
            {
                failure = new ServerTestFailure
                {
                    // The fixture directory is unique; a local ordinal avoids a redundant UUID
                    // in each filename and preserves room for the runner's results path on net472.
                    Id = (_failures.Count + 1).ToString(CultureInfo.InvariantCulture),
                    Exception = exception,
                    Test = new FailureDiagnosticText(512).Bound(test),
                    Invocation = identity,
                    Details = new FailureDiagnosticText(4_096).Bound(details),
                };
                _failures.Add(failure);
            }

            return _fileSystem.Path.Combine(Directory, failure.Id + ".log");
        }
    }

    public bool Report(Exception exception, string invocation)
    {
        lock (_gate)
        {
            var failure = _failures.Single(item => item.Invocation == invocation && ReferenceEquals(item.Exception, exception));
            var firstReport = !failure.Reported;
            failure.Reported = true;
            return firstReport;
        }
    }

    public bool IsReported(Exception exception)
    {
        lock (_gate)
        {
            return _failures.Any(item => item.Reported && ReferenceEquals(item.Exception, exception));
        }
    }

    public async Task WriteMetadataAsync(string mode, string process, IReadOnlyList<Exception> lifecycleFailures)
    {
        _ = _fileSystem.Directory.CreateDirectory(Directory);
        foreach (var failure in Failures)
        {
            var text = new StringBuilder("test=" + failure.Test + "\ninvocation=" + new FailureDiagnosticText(256).Bound(failure.Invocation) +
                       "\nassembly=" + _assembly + "\nframework=" + Framework + "\nmode=" + mode +
                       "\nserver-process=" + process + "\n" + failure.Details + "\nprimary:\n" +
                       new FailureDiagnosticText(8_192).Describe(failure.Exception));
            foreach (var lifecycleFailure in lifecycleFailures.Take(64).Where(item => !ReferenceEquals(item, failure.Exception)))
            {
                _ = text.Append("\nlifecycle:\n").Append(new FailureDiagnosticText(2_048).Describe(lifecycleFailure));
            }

            if (lifecycleFailures.Count > 64)
            {
                _ = text.Append("\n[truncated]");
            }

            await new DiagnosticFileWriter(_fileSystem).WriteAsync(
                _fileSystem.Path.Combine(Directory, failure.Id + ".log"), _text.Bound(text.ToString()));
        }
    }
}
