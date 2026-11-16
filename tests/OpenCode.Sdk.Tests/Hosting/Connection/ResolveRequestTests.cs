using NSubstitute;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Hosting.Connection;

public sealed class ResolveRequestTests
{
    private readonly IServerDoors _doors = Substitute.For<IServerDoors>();
    private readonly IServiceFileSystem _files = Substitute.For<IServiceFileSystem>();
    private readonly IServiceEnvironment _environment = Substitute.For<IServiceEnvironment>();

    [Test]
    [Arguments("conflict")]
    [Arguments("password")]
    [Arguments("service")]
    public async Task ResolveAsync_Should_Return_Argument_Failures_Through_The_Task(string invalid)
    {
        var options = invalid switch
        {
            "conflict" => new OpenCodeServerResolveOptions { Endpoint = new Uri("http://127.0.0.1:4096"), Standalone = true },
            "password" => new OpenCodeServerResolveOptions { Password = "pw" },
            _ => new OpenCodeServerResolveOptions { Service = new OpenCodeServerEnsureOptions { VersionPolicy = OpenCodeServerVersionPolicy.Replace } },
        };
        List<Task<OpenCodeServerConnection>> resolving = [];
        void Invoke() => resolving.Add(OpenCodeServer.ResolveAsync(options));

        await Assert.That(Invoke).ThrowsNothing();
        await Assert.That(resolving.Count).IsEqualTo(1);
        var failure = await Assert.That(resolving[0]).Throws<ArgumentException>();
        await Assert.That(failure!.ParamName).IsEqualTo("options");
    }

    [Test]
    public async Task Snapshot_Should_Refuse_An_Endpoint_With_Standalone()
    {
        var failure = await Assert.That(() => ResolveRequest.Snapshot(new OpenCodeServerResolveOptions
        {
            Endpoint = new Uri("http://127.0.0.1:4096"),
            Standalone = true,
        })).Throws<ArgumentException>();

        await Assert.That(failure!.ParamName).IsEqualTo("options");
        await Assert.That(failure.Message).IsEqualTo(
            "OpenCodeServerResolveOptions.Endpoint and Standalone cannot be combined. (Parameter 'options')");
    }

    [Test]
    [Arguments(true, true)]
    [Arguments(false, false)]
    [Arguments(false, true)]
    public async Task Snapshot_Should_Refuse_A_Password_Without_An_Endpoint(bool standalone, bool autostart)
    {
        var failure = await Assert.That(async () => _ = await OpenCodeServer.ResolveWithSeamsAsync(
            new OpenCodeServerResolveOptions { Password = "pw", Standalone = standalone, Autostart = autostart },
            new ConnectionSeams(_doors, _environment, _files), CancellationToken.None)).Throws<ArgumentException>();

        await Assert.That(failure!.ParamName).IsEqualTo("options");
        await Assert.That(failure.Message).IsEqualTo(
            "Password applies only to an explicit Endpoint; a standalone or service connection carries its own credentials. (Parameter 'options')");
        await Assert.That(_doors.ReceivedCalls()).IsEmpty();
        await Assert.That(_files.ReceivedCalls()).IsEmpty();
        await Assert.That(_environment.ReceivedCalls()).IsEmpty();
    }

    [Test]
    [Arguments("relative")]
    [Arguments("ftp://host/path")]
    [Arguments("http://host/?query=1")]
    [Arguments("http://host/#fragment")]
    [Arguments("http://user@host/")]
    public async Task Snapshot_Should_Refuse_An_Endpoint_The_Client_Would_Refuse(string endpoint)
    {
        _ = await Assert.That(() => ResolveRequest.Snapshot(new OpenCodeServerResolveOptions
        {
            Endpoint = new Uri(endpoint, UriKind.RelativeOrAbsolute),
        })).Throws<ArgumentException>();
    }

    [Test]
    public async Task ResolveAsync_Should_Refuse_Invalid_Service_Options_Before_Any_Io_On_A_Managed_Path()
    {
        var failure = await Assert.That(async () => _ = await OpenCodeServer.ResolveWithSeamsAsync(
            new OpenCodeServerResolveOptions { Service = new OpenCodeServerEnsureOptions { VersionPolicy = OpenCodeServerVersionPolicy.Replace } },
            new ConnectionSeams(_doors, _environment, _files), CancellationToken.None)).Throws<ArgumentException>();

        await Assert.That(failure!.Message).IsEqualTo(
            "OpenCodeServerEnsureOptions.VersionPolicy Replace needs ExpectedVersion; without it there is nothing to mismatch. (Parameter 'options')");
        await Assert.That(_files.ReceivedCalls()).IsEmpty();
        await Assert.That(_environment.ReceivedCalls()).IsEmpty();
        await Assert.That(_doors.ReceivedCalls()).IsEmpty();
    }
}
