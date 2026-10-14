using System.Net.WebSockets;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Terminal;

/// <summary>
/// The shipped adapter's upgrade contract. The adapter is family-neutral, so the wording of a
/// refused upgrade is not its to invent: what it owes is the guard that refuses to run without a
/// policy, and the routing that hands a real upgrade failure to the policy it was given.
/// </summary>
public sealed class ClientTerminalWebSocketTests
{
    private const string TerminalId = "pty_100";

    /// <summary>
    /// Loopback port 1 is the address nothing in this repository ever binds, so the upgrade is
    /// refused by the kernel on the first connect - no DNS, no listener, no wall-clock wait.
    /// </summary>
    private static readonly Uri Unreachable = new("ws://127.0.0.1:1/api/pty/pty_100/connect");

    [Test]
    public async Task ConnectAsync_Should_Refuse_A_Missing_Upgrade_Failure_Policy()
    {
        using var socket = new ClientTerminalWebSocket(authorization: null);

        var failure = await Assert
            .That(async () => await socket.ConnectAsync(Unreachable, TerminalId, policy: null!, CancellationToken.None))
            .Throws<ArgumentNullException>();

        await Assert.That(failure!.ParamName).IsEqualTo("policy");
    }

    [Test]
    public async Task ConnectAsync_Should_Route_A_Refused_Upgrade_Through_The_Injected_Policy()
    {
        using var socket = new ClientTerminalWebSocket(authorization: null);
        var policy = new RecordingUpgradeFailurePolicy();

        var failure = await Assert
            .That(async () => await socket.ConnectAsync(Unreachable, TerminalId, policy, CancellationToken.None))
            .Throws<OpenCodeTransportException>();

        // The failure the caller sees is the policy's own, carrying the terminal the adapter was
        // told about: the family owns the wording, the adapter owns only the upgrade.
        await Assert.That(failure!.Message).IsEqualTo(RecordingUpgradeFailurePolicy.Message);
        await Assert.That(policy.Calls).IsEqualTo(1);
        await Assert.That(policy.MappedTerminalId).IsEqualTo(TerminalId);
    }

    /// <summary>Records what the adapter handed the upgrade-failure seam.</summary>
    private sealed class RecordingUpgradeFailurePolicy : ITerminalUpgradeFailurePolicy
    {
        public const string Message = "The scripted upgrade-failure policy named this refusal.";

        public int Calls { get; private set; }

        public string? MappedTerminalId { get; private set; }

        public OpenCodeTransportException Map(WebSocketException exception, int? status, string terminalId)
        {
            Calls++;
            MappedTerminalId = terminalId;
            return new OpenCodeTransportException(Message, exception);
        }
    }
}
