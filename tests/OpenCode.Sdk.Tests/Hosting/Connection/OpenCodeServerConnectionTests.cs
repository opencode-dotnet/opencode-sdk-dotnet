using NSubstitute;
using OpenCode.Sdk.Internal;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Hosting.Connection;

public sealed class OpenCodeServerConnectionTests
{
    private static readonly Uri Endpoint = new("http://127.0.0.1:4096");

    [Test]
    [Arguments(OpenCodeServerConnectionMode.Explicit)]
    [Arguments(OpenCodeServerConnectionMode.Standalone)]
    [Arguments(OpenCodeServerConnectionMode.Service)]
    [Arguments(OpenCodeServerConnectionMode.Explicit, "Username")]
    [Arguments(OpenCodeServerConnectionMode.Standalone, "Password")]
    [Arguments(OpenCodeServerConnectionMode.Service, "Endpoint")]
    public async Task CreateClient_Should_Refuse_Identity_Members_In_Every_Mode(
        OpenCodeServerConnectionMode mode, string member = "Endpoint")
    {
        await using var server = new OpenCodeServer(Endpoint, "pw");
        await using var connection = Connection(mode, server);

        var failure = await Assert.That(() => connection.CreateClient(options =>
        {
            switch (member)
            {
                case "Endpoint": options.Endpoint = Endpoint; break;
                case "Username": options.Username = "other"; break;
                case "Password": options.Password = "other"; break;
            }
        })).Throws<InvalidOperationException>();

        await Assert.That(failure!.Message).IsEqualTo(
            "CreateClient owns the connection identity: leave Endpoint, Username, and Password unset — the started server supplies them. Configure behavior members such as Location only.");
    }

    [Test]
    [Arguments(OpenCodeServerConnectionMode.Explicit)]
    [Arguments(OpenCodeServerConnectionMode.Standalone)]
    [Arguments(OpenCodeServerConnectionMode.Service)]
    public async Task DisposeAsync_Should_End_Only_An_Owned_Standalone_Server(OpenCodeServerConnectionMode mode)
    {
        var server = Substitute.For<OpenCodeServer>();
        server.Endpoint.Returns(Endpoint);
        server.Password.Returns("pw");
        var connection = Connection(mode, server);

        await connection.DisposeAsync();
        await connection.DisposeAsync();

        string[] expected = mode == OpenCodeServerConnectionMode.Standalone ? ["DisposeAsync"] : [];
        await Assert.That(server.ReceivedCalls().Where(call => call.GetMethodInfo().Name == "DisposeAsync")
            .Select(call => call.GetMethodInfo().Name))
            .IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Members_Should_Throw_The_Instructive_Mock_Failure_When_Not_Overridden()
    {
        var mock = Substitute.ForPartsOf<OpenCodeServerConnection>();
        string[] members = ["Mode", "Endpoint", "Username", "Password", "ServerVersion", "Server", "ServiceOperations"];
        foreach (var member in members)
        {
            var property = typeof(OpenCodeServerConnection).GetProperty(member)!;
            var failure = await Assert.That(() => property.GetValue(mock)).Throws<System.Reflection.TargetInvocationException>();
            await Assert.That(failure!.InnerException!.Message).IsEqualTo(MockSeam.CreateError(nameof(OpenCodeServerConnection), member).Message);
        }
    }

    private static OpenCodeServerConnection Connection(OpenCodeServerConnectionMode mode, OpenCodeServer server) => mode switch
    {
        OpenCodeServerConnectionMode.Explicit => new OpenCodeServerConnection(Endpoint, "pw", "2.3.0"),
        OpenCodeServerConnectionMode.Standalone => new OpenCodeServerConnection(server, serverVersion: null),
        OpenCodeServerConnectionMode.Service => new OpenCodeServerConnection(server, "2.2.7", Substitute.For<OpenCodeServerServiceOperations>()),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}
