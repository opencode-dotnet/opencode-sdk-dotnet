using NSubstitute;
using OpenCode.Sdk.Internal;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Hosting.Connection;

public sealed class OpenCodeServerServiceOperationsTests
{
    private static readonly Uri Endpoint = new("http://127.0.0.1:4096");
    private readonly IServerDoors _doors = Substitute.For<IServerDoors>();
    private readonly ManagedServiceSnapshot _snapshot = new(
        EnsureRequest.Snapshot(new OpenCodeServerEnsureOptions
        {
            ExpectedVersion = "2.2.7",
            VersionPolicy = OpenCodeServerVersionPolicy.Replace,
            Command = ["opencode", "serve", "--service"],
            Environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["A"] = "1" },
            OnStart = _ => throw new InvalidOperationException("must not forward"),
        }),
        new ServicePaths("registration.json", "config.json", [], null),
        new ServiceConfigEnvironment(Entries: null));

    [Test]
    public async Task ReconnectAsync_Should_Return_Pre_Cancellation_Through_The_Task()
    {
        var token = new CancellationToken(canceled: true);
        var operations = new OpenCodeServerServiceOperations(_snapshot, _doors);
        List<Task<OpenCodeServer>> reconnecting = [];
        void Invoke() => reconnecting.Add(operations.ReconnectAsync(token));

        await Assert.That(Invoke).ThrowsNothing();
        await Assert.That(reconnecting.Count).IsEqualTo(1);
        var failure = await Assert.That(reconnecting[0]).Throws<OperationCanceledException>();
        await Assert.That(failure!.CancellationToken).IsEqualTo(token);
        await Assert.That(_doors.ReceivedCalls()).IsEmpty();
    }

    [Test]
    public async Task ReconnectAsync_Should_Ensure_The_Snapshot_With_Ignore_And_No_OnStart()
    {
        await using var replacement = Replacement();
        using var caller = new CancellationTokenSource();
        ReturnOnEnsure(replacement, caller.Token);

        var result = await new OpenCodeServerServiceOperations(_snapshot, _doors).ReconnectAsync(caller.Token);

        await Assert.That(result).IsSameReferenceAs(replacement);
        var calls = _doors.ReceivedCalls().ToArray();
        await Assert.That(calls.Select(call => call.GetMethodInfo().Name)).IsEquivalentTo(["EnsureLocatedAsync"], CollectionOrdering.Matching);
        var arguments = calls[0].GetArguments();
        var request = (EnsureRequest)arguments[0]!;
        await Assert.That(request.Policy).IsEqualTo(OpenCodeServerVersionPolicy.Ignore);
        await Assert.That(request.LoopVersion).IsNull();
        await Assert.That(request.OnStart).IsNull();
        await Assert.That(request.Command).IsEquivalentTo(["opencode", "serve", "--service"], CollectionOrdering.Matching);
        await Assert.That(request.Environment).IsEquivalentTo([new KeyValuePair<string, string>("A", "1")], CollectionOrdering.Matching);
        await Assert.That(arguments[1]).IsSameReferenceAs(_snapshot.Paths);
        await Assert.That(arguments[2]).IsSameReferenceAs(_snapshot.Overlay);
        await Assert.That(arguments[3]).IsEqualTo(caller.Token);
    }

    [Test]
    public async Task RestartAsync_Should_Stop_With_Handoff_Then_Ensure()
    {
        await using var replacement = Replacement();
        using var caller = new CancellationTokenSource();
        ReturnOnEnsure(replacement, caller.Token);

        var result = await new OpenCodeServerServiceOperations(_snapshot, _doors).RestartAsync(caller.Token);

        await Assert.That(result).IsSameReferenceAs(replacement);
        var calls = _doors.ReceivedCalls().ToArray();
        await Assert.That(calls.Select(call => call.GetMethodInfo().Name))
            .IsEquivalentTo(["StopLocatedAsync", "EnsureLocatedAsync"], CollectionOrdering.Matching);
        await Assert.That(calls[0].GetArguments()).IsEquivalentTo(
            new object?[] { _snapshot.Paths, OpenCodeServerStopPtyMode.Handoff, caller.Token }, CollectionOrdering.Matching);
        _ = _doors.Received(1).EnsureLocatedAsync(_snapshot.ReconnectRequest, _snapshot.Paths, _snapshot.Overlay, caller.Token);
    }

    [Test]
    public async Task RestartAsync_Should_Not_Ensure_When_The_Stop_Fails()
    {
        var cause = new OpenCodeServerException("stop failed");
        _doors.StopLocatedAsync(_snapshot.Paths, OpenCodeServerStopPtyMode.Handoff, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(cause));

        var failure = await Assert.That(async () => _ = await new OpenCodeServerServiceOperations(_snapshot, _doors).RestartAsync())
            .Throws<OpenCodeServerException>();

        await Assert.That(failure).IsSameReferenceAs(cause);
        await Assert.That(_doors.ReceivedCalls().Select(call => call.GetMethodInfo().Name))
            .IsEquivalentTo(["StopLocatedAsync"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task RestartAsync_Should_Refuse_A_Cancelled_Call_Before_Stopping()
    {
        var token = new CancellationToken(canceled: true);

        var failure = await Assert.That(async () => _ = await new OpenCodeServerServiceOperations(_snapshot, _doors).RestartAsync(token))
            .Throws<OperationCanceledException>();

        await Assert.That(failure!.CancellationToken).IsEqualTo(token);
        await Assert.That(_doors.ReceivedCalls()).IsEmpty();
    }

    private static OpenCodeServer Replacement() => new(Endpoint, "pw", 42, "2.3.0");

    private void ReturnOnEnsure(OpenCodeServer replacement, CancellationToken token) =>
        _doors.EnsureLocatedAsync(Arg.Any<EnsureRequest>(), _snapshot.Paths, _snapshot.Overlay, token).Returns(replacement);

    [Test]
    public async Task ServerVersion_Should_Keep_The_Resolved_Value_After_Reconnect_And_Restart()
    {
        var operations = new OpenCodeServerServiceOperations(_snapshot, _doors);
        await using var resolved = new OpenCodeServer(Endpoint, "pw", 41, "2.2.7");
        await using var connection = new OpenCodeServerConnection(resolved, "2.2.7", operations);
        await using var replacement = new OpenCodeServer(Endpoint, "pw", 42, "2.3.0");
        _doors.EnsureLocatedAsync(Arg.Any<EnsureRequest>(), _snapshot.Paths, _snapshot.Overlay, Arg.Any<CancellationToken>())
            .Returns(replacement);

        var reconnected = await operations.ReconnectAsync();
        var restarted = await operations.RestartAsync();

        await Assert.That(connection.ServerVersion).IsEqualTo("2.2.7");
        await Assert.That(reconnected.RegisteredVersion).IsEqualTo("2.3.0");
        await Assert.That(restarted.RegisteredVersion).IsEqualTo("2.3.0");
        await Assert.That(connection.Server).IsSameReferenceAs(resolved);
        await Assert.That(connection.Endpoint).IsEqualTo(Endpoint);
    }
}
