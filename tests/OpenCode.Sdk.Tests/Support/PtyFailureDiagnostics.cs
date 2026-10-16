using System.Globalization;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Harness.Ownership;
using OpenCode.Sdk.Tests.Harness.Ownership.Abstractions;

namespace OpenCode.Sdk.Tests.Support;

internal sealed class PtyFailureDiagnostics(IOwnedOperationDeadline? deadline = null)
{
    private readonly FailureDiagnosticText _text = new();
    private string _phase = "initialize";
    private string? _id;
    private long? _pid;
    private int? _createStatus;
    private PtyLiveTranscript? _transcript;
    private string _status = "not available: no PTY identity";

    public LateCleanupFailureReport? LateFailures { get; private set; }

    public void Created(string id, long pid, int status)
    {
        _id = id;
        _pid = pid;
        _createStatus = status;
    }

    public void Enter(string phase, PtyLiveTranscript? transcript = null)
    {
        _phase = phase;
        _transcript = transcript;
    }

    public async Task CaptureStatusAsync(PinnedOpenCodeServerFixture server, LocationSelector? location, Exception primary)
    {
        if (_id is null)
        {
            return;
        }

        var diagnostics = new OwnedCleanup(TimeSpan.FromSeconds(3), deadline ?? new OwnedOperationDeadline());
        _status = "lookup did not finish within its diagnostic budget";
        diagnostics.Own("PTY failure status lookup", async token =>
        {
            // The independent client stays with this operation if its observation deadline wins.
            using var client = server.CreateClient(location);
            var response = await client.Ptys.GetPtyClient(_id).GetAsync(
                new PtyRequest { Location = location }, OpenCodeRequestOptions.NoThrow, token);
            _status = "http=" + response.Status.ToString(CultureInfo.InvariantCulture) +
                      " status=" + (response.IsError ? "unavailable" : response.Pty.Status.ToString()) +
                      " pid=" + (response.IsError ? "unavailable" : response.Pty.Pid.ToString(CultureInfo.InvariantCulture));
        });
        try
        {
            await diagnostics.CompleteAsync(primary);
        }
        catch (Exception exception) when (ReferenceEquals(exception, primary))
        {
            // The original failure now carries any immediate/late lookup errors; cleanup follows.
            primary.Data["PtyFailureDiagnostics.Status"] = _status;
        }
        finally
        {
            LateFailures = diagnostics.LateFailures;
        }
    }

    public string Describe() => _text.Bound(
        "pty-phase=" + _phase + "\npty-id=" + (_id ?? "not-created") +
        "\npty-pid=" + (_pid?.ToString(CultureInfo.InvariantCulture) ?? "not-created") +
        "\npty-create-http=" + (_createStatus?.ToString(CultureInfo.InvariantCulture) ?? "no-response") +
        "\npty-status=" + _status + "\nlast-observation=" + (_transcript?.Describe() ?? "no-transcript"));
}
