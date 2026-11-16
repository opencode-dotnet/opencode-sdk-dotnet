using System.Reflection;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Hosting;

public sealed class ClientIdentityComposerTests
{
    [Test]
    public async Task CreateClient_Should_Copy_Behavior_And_Leave_The_Delegate_Options_Identity_Unset()
    {
        var endpoint = new Uri("http://127.0.0.1:4096");
        var location = new LocationSelector { Directory = "/workspace" };
        OpenCodeClientOptions? observed = null;
        using var client = ClientIdentityComposer.CreateClient(endpoint, "pw", options =>
        {
            observed = options;
            options.Location = location;
            options.NetworkTimeout = TimeSpan.FromSeconds(7);
            options.EventStreamIdleTimeout = TimeSpan.FromSeconds(9);
        });
        var pipeline = (Pipeline)typeof(OpenCodeClient).GetField("_pipeline", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
        var exchange = typeof(Pipeline).GetField("_exchange", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pipeline)!;

        await Assert.That(observed).IsNotNull();
        await Assert.That(observed.Endpoint).IsNull();
        await Assert.That(observed.Password).IsNull();
        await Assert.That(pipeline.Connection.EndpointBase).IsEqualTo(endpoint.AbsoluteUri.TrimEnd('/'));
        await Assert.That(pipeline.Connection.Authorization?.ToString()).IsEqualTo("Basic b3BlbmNvZGU6cHc=");
        await Assert.That(pipeline.Connection.Location).IsEqualTo(location);
        await Assert.That(typeof(PipelineExchange).GetField("_clientNetworkTimeout", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(exchange)).IsEqualTo(TimeSpan.FromSeconds(7));
        await Assert.That(pipeline.IdleWindow).IsEqualTo(TimeSpan.FromSeconds(9));
    }
}
