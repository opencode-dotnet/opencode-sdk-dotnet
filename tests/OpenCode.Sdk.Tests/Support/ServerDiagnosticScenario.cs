using System.IO.Abstractions;
using NSubstitute;
using OpenCode.Sdk.TestSupport;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests.Support;

internal sealed class ServerDiagnosticScenario
{
    private readonly RealFileSystem _fileSystem = new();
    private readonly FixtureLoader _fixtures = new();
    private readonly IFileSystem _captureFileSystem;

    public ServerDiagnosticScenario()
    {
        _captureFileSystem = Substitute.For<IFileSystem>();
        _captureFileSystem.Path.Returns(_fileSystem.Path);
        _captureFileSystem.Directory.Returns(_fileSystem.Directory);
        _captureFileSystem.File.CreateText(Arg.Any<string>()).Returns(call =>
        {
            var path = call.ArgAt<string>(0);
            if (_fileSystem.Path.GetFileName(path) == "stderr.log")
            {
                CaptureBarrier?.Block();
                if (CaptureFailure is not null)
                {
                    throw CaptureFailure;
                }
            }

            return _fileSystem.File.CreateText(path);
        });
    }

    /// <summary>
    /// Gets the last stdout line the diagnostic peer's final collection holds. On Windows disposal
    /// closes the stdin lease and then drains, so the lines the peer writes on its way out are
    /// collected. On Linux and macOS the collection closes as disposal starts and stdin closes last,
    /// so the final collection ends with what the peer wrote while it ran.
    /// </summary>
    public static string FinalStandardOutput => OperatingSystem.IsWindows() ? "FINAL-STDOUT" : "BODY-STDOUT";

    /// <summary>Gets the last stderr line the diagnostic peer's final collection holds; see <see cref="FinalStandardOutput"/>.</summary>
    public static string FinalStandardError => OperatingSystem.IsWindows() ? "FINAL-STDERR" : "BODY-STDERR";

    public Exception? CaptureFailure { get; set; }

    public DiagnosticWriteBarrier? CaptureBarrier { get; set; }

    public bool RetainLogs { get; init; }

    public OperationDeadlineScenario Deadlines { get; } = new();

    public PinnedOpenCodeServerFixture CreateFixture(string peer = "Server.diagnostic-peer.js") => new(
        ["bun", "-e", _fixtures.LoadText(peer)],
        new PinnedServerCommand(_fileSystem).RepositoryRoot,
        new ServerFixtureDiagnosticsOptions
        {
            FileSystem = _captureFileSystem,
            ResultsDirectory = new TestResultsDirectory(_fileSystem).Resolve(Environment.GetCommandLineArgs()),
            Deadline = Deadlines.Deadline,
            RetainLogs = RetainLogs,
        });

    public async Task DisposeFixtureAsync(PinnedOpenCodeServerFixture fixture, Task completion)
    {
        try
        {
            await Deadlines.DrainAsync(completion);
            await Deadlines.DrainAsync(fixture.DisposeAsync().AsTask());
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await fixture.DrainDiagnosticsAsync(budget.Token);
        }
        finally
        {
            fixture.RunRoot.Dispose();
        }
    }

    public async Task<string> ReadLogAsync(PinnedOpenCodeServerFixture fixture, string name)
    {
        using var reader = _fileSystem.File.OpenText(_fileSystem.Path.Combine(fixture.DiagnosticsDirectory, name));
        return await reader.ReadToEndAsync();
    }

    public async Task<string> ReadMetadataAsync(PinnedOpenCodeServerFixture fixture)
    {
        var texts = new List<string>();
        foreach (var path in _fileSystem.Directory.GetFiles(fixture.DiagnosticsDirectory, "*.log"))
        {
            if (_fileSystem.Path.GetFileName(path) is not "stdout.log" and not "stderr.log")
            {
                using var reader = _fileSystem.File.OpenText(path);
                texts.Add(await reader.ReadToEndAsync());
            }
        }

        return string.Join(Environment.NewLine, texts);
    }
}
