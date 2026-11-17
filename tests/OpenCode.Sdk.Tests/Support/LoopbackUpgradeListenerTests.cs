using System.Globalization;
using System.Net;
using System.Net.Sockets;
using OpenCode.Sdk.Tests.Harness;

namespace OpenCode.Sdk.Tests.Support;

public sealed class LoopbackUpgradeListenerTests
{
    [Test]
    public async Task Listener_Should_Skip_A_Port_A_Plain_Tcp_Socket_Holds_And_Start_On_The_Next_Port()
    {
        using var held = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        held.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        held.Listen(1);
        var port = ((IPEndPoint)held.LocalEndPoint!).Port; // Bind supplies the local endpoint.
        using var direct = new HttpListener();
        direct.Prefixes.Add($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/");
        var conflict = Assert.Throws<HttpListenerException>(direct.Start);
        // Managed Unix listeners report the socket bind error, not the HTTP.sys conflict handled here.
        if (OperatingSystem.IsWindows())
        {
            await Assert.That(conflict.ErrorCode).IsEqualTo(32);
            await AssertStartsOnTheNextPortAsync(port);
        }
        else
        {
            using var scenario = new LoopbackUpgradeListener(() => port);
            await Assert.That(() => _ = scenario.Listener).ThrowsExactly<HttpListenerException>();
        }
    }

    [Test]
    public async Task Listener_Should_Skip_A_PreRegistered_Conflict_And_Start_On_The_Next_Port()
    {
        using var owner = new LoopbackUpgradeListener();
        var prefix = owner.Listener.Prefixes.Single();
        using var duplicate = new HttpListener();
        duplicate.Prefixes.Add(prefix);
        var conflict = Assert.Throws<HttpListenerException>(duplicate.Start);
        await Assert.That(conflict.ErrorCode).IsEqualTo(OperatingSystem.IsWindows() ? 183 : 400);
        if (OperatingSystem.IsWindows())
        {
            await AssertStartsOnTheNextPortAsync(new Uri(prefix).Port);
        }
        else
        {
            using var scenario = new LoopbackUpgradeListener(() => new Uri(prefix).Port);
            await IsWindowsListenerAsync(scenario);
        }
    }

    [Test]
    public async Task Listener_Should_Propagate_Any_Other_HttpListenerException()
    {
        var failure = new HttpListenerException(5);
        using var scenario = new LoopbackUpgradeListener(() => throw failure);
        var propagated = Assert.Throws<HttpListenerException>(() => _ = scenario.Listener);
        await Assert.That(propagated).IsSameReferenceAs(failure);
        var repeated = Assert.Throws<HttpListenerException>(() => _ = scenario.Listener);
        await Assert.That(repeated).IsSameReferenceAs(failure);
    }

    [Test]
    public async Task Listener_Should_Name_Every_Tried_Port_When_Reservations_Are_Exhausted()
    {
        using var first = new LoopbackUpgradeListener();
        using var second = new LoopbackUpgradeListener();
        var a = new Uri(first.Listener.Prefixes.Single()).Port;
        var b = new Uri(second.Listener.Prefixes.Single()).Port;
        var ports = new Queue<int>([a, b, a, b, a]);
        using var scenario = new LoopbackUpgradeListener(ports.Dequeue);
        if (await IsWindowsListenerAsync(scenario))
        {
            var failure = Assert.Throws<InvalidOperationException>(() => _ = scenario.Listener);
            await Assert.That(failure.Message).IsEqualTo(string.Format(CultureInfo.InvariantCulture,
                "No loopback listener port could be registered: {0}, {1}, {0}, {1}, {0}.", a, b));
        }
    }

    private static async Task AssertStartsOnTheNextPortAsync(int blocked)
    {
        var next = EnsureServiceContext.ReserveUnguardedPort(LoopbackPortReservation.Hold);
        var ports = new Queue<int>([blocked, next]);
        using var scenario = new LoopbackUpgradeListener(ports.Dequeue);
        await Assert.That(() => scenario.Listener).ThrowsNothing();
        await Assert.That(new Uri(scenario.Listener.Prefixes.Single()).Port).IsEqualTo(next);
        await Assert.That(scenario.Listener.IsListening).IsTrue();
    }

    private static async Task<bool> IsWindowsListenerAsync(LoopbackUpgradeListener scenario)
    {
        if (!OperatingSystem.IsWindows())
        {
            var propagated = await Assert.That(() => _ = scenario.Listener).ThrowsExactly<HttpListenerException>();
            await Assert.That(propagated!.ErrorCode).IsEqualTo(400);
        }
        return OperatingSystem.IsWindows();
    }
}
