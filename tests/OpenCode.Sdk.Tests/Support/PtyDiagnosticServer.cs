using System.Net;
using OpenCode.Sdk.Tests.Harness;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>Serves a healthy external endpoint with either a completed or stalled typed PTY response.</summary>
internal sealed class PtyDiagnosticServer : IAsyncDisposable
{
    private readonly TaskCompletionSource<bool> _requested = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PtyDiagnosticServer(bool stall)
    {
        var payload = new FixtureLoader().LoadJson("Serialization.known-pty.json");
        Server = LoopbackHttpServer.Start(path =>
        {
            if (path == "/api/info")
            {
                return new LoopbackHttpResponse
                {
                    StatusCode = HttpStatusCode.OK,
                    ContentType = "application/json",
                    Body = WireBodyData.InfoOk,
                };
            }

            _ = _requested.TrySetResult(true);
            return new LoopbackHttpResponse
            {
                StatusCode = HttpStatusCode.OK,
                ContentType = "application/json",
                Body = WireBodyData.LocationEnvelope(payload),
                KeepOpen = stall,
            };
        });
    }

    public LoopbackHttpServer Server { get; }

#pragma warning disable VSTHRD003 // Test signals complete on their own source and resume asynchronously; exposing the task is the signal's contract.
    public Task Requested => _requested.Task;
#pragma warning restore VSTHRD003

    public async ValueTask DisposeAsync() => await Server.DisposeAsync();
}
