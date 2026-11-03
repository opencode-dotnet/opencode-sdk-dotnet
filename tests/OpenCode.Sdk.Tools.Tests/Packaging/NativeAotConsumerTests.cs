using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tools.Generator.Refresh;
using OpenCode.Sdk.Tools.Generator.Refresh.Abstractions;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tools.Tests.Packaging;

/// <summary>
/// The packed packages publish a native AOT consumer cleanly, and the published consumer runs:
/// one real consumer of both packages, compiled by ILC, proves the trim and AOT annotations the
/// packages carry — the configure-action registration, the source-generated serialization — hold
/// for the shape a native consumer actually builds. The publish loads every core of the machine
/// for about a minute, so the class carries the <see cref="TestCategories.NativeAot"/> category
/// and runs in a pass of its own, away from the timing-sensitive launcher tests; the three tests
/// share one publish through the session-shared fixture. No skip: a machine without the native
/// toolchain fails with the compiler's own message.
/// </summary>
[Category(TestCategories.NativeAot)]
[ClassDataSource<PackedConsumerFeed>(Shared = SharedType.PerTestSession)]
public sealed partial class NativeAotConsumerTests(PackedConsumerFeed feed)
{
    private static readonly RealFileSystem FileSystem = new();

    private readonly CliWrapProcessRunner _runner = new();

    [Test]
    [Timeout(600_000)]
    public async Task Publishing_A_Native_Aot_Consumer_Should_Exit_Zero(CancellationToken cancellationToken)
    {
        var publish = await feed.PublishConsumerAsync(cancellationToken);

        await Assert.That(publish.ExitCode).IsEqualTo(0).Because(publish.Output);
    }

    [Test]
    [Timeout(600_000)]
    public async Task Publishing_A_Native_Aot_Consumer_Should_Report_No_Trimming_Or_Aot_Warnings(CancellationToken cancellationToken)
    {
        var publish = await feed.PublishConsumerAsync(cancellationToken);
        var warnings = AotWarningCodes()
            .Matches(publish.Output)
            .Select(static match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        await Assert.That(warnings).IsEmpty().Because("the publish reported " + string.Join(", ", warnings));
    }

    [Test]
    [Timeout(600_000)]
    public async Task The_Published_Native_Aot_Consumer_Should_Print_Its_Marker_And_Exit_Zero(CancellationToken cancellationToken)
    {
        var publish = await feed.PublishConsumerAsync(cancellationToken);
        await Assert.That(publish.Executable).Satisfies(FileSystem.File.Exists)
            .Because("the publish produced no native executable:\n" + publish.Output);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var endpoint = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);
        await using var framesSource = typeof(NativeAotConsumerTests).Assembly
            .GetManifestResourceStream("OpenCode.Sdk.Tests.Fixtures.Serialization.reader-prompt-events.json")
            ?? throw new InvalidOperationException("Missing prompt-event fixture.");
        using var frames = await JsonDocument.ParseAsync(framesSource, cancellationToken: cancellationToken);
        var running = RunAsync();
        var path = await ServeAsync(listener, new FixtureLoader().Load("Packaging.reader-tool-states.json"), "application/json", shutdown.Token);
        var eventPath = await ServeAsync(listener,
            "data: " + Encoding.UTF8.GetString(Performance.Tests.Support.BenchmarkFixtures.ConnectedEventBody()) + "\n\n"
            + string.Concat(frames.RootElement.EnumerateArray().Select(static frame => "data: " + JsonSerializer.Serialize(frame) + "\n\n")),
            "text/event-stream", shutdown.Token);
        var run = await running;
        await Assert.That(path).IsEqualTo("/api/session/ses_reader/message/msg_reader");
        await Assert.That(eventPath).IsEqualTo("/api/event");

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.StandardError + run.StandardOutputText);
        await Assert.That(run.StandardOutputText.Replace("\r\n", "\n", StringComparison.Ordinal))
            .IsEqualTo(AotConsumerData.Marker + "\n")
            .Because("standard output was: " + run.StandardOutputText);

        async Task<ProcessResult> RunAsync()
        {
            try
            {
                return await _runner.RunAsync(publish.Executable, [endpoint],
                    FileSystem.Path.GetDirectoryName(publish.Executable) ?? publish.Executable, cancellationToken);
            }
            finally
            {
                await shutdown.CancelAsync();
            }
        }
    }

    private static async Task<string?> ServeAsync(TcpListener listener, string body, string contentType, CancellationToken cancellationToken)
    {
        try
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var request = await reader.ReadLineAsync(cancellationToken);
            while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 })
            {
                // Drain the request head; this canned GET response has no request body.
            }
            var bytes = Encoding.UTF8.GetBytes(body);
            var head = "HTTP/1.1 200 OK\r\nContent-Type: " + contentType + "\r\nContent-Length: "
                + bytes.Length.ToString(CultureInfo.InvariantCulture) + "\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancellationToken);
            await stream.WriteAsync(bytes, cancellationToken);
            return request?.Split(' ')[1];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null; // Process exit without a request is asserted by the caller, not a hang verdict.
        }
    }

    /// <summary>The warning codes the trim analyzer (<c>IL2xxx</c>) and the AOT compiler (<c>IL3xxx</c>) emit.</summary>
    [GeneratedRegex(@"\bIL[23]\d{3}\b", RegexOptions.None, Timeout.Infinite)]
    private static partial Regex AotWarningCodes();
}
