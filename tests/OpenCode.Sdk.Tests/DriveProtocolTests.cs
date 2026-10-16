using System.Text.Json;
using OpenCode.Sdk.Tests.Harness;

namespace OpenCode.Sdk.Tests;

public sealed class DriveProtocolTests
{
    private static JsonDocument Parse(byte[] message) => JsonDocument.Parse(message);

    [Test]
    public async Task Handshake_Should_Offer_Version_One_As_A_Backend_Controller()
    {
        using var message = Parse(DriveProtocol.Handshake(id: 1));
        var root = message.RootElement;

        await Assert.That(root.GetProperty("jsonrpc").GetString()).IsEqualTo("2.0");
        await Assert.That(root.GetProperty("id").GetInt64()).IsEqualTo(1L);
        await Assert.That(root.GetProperty("method").GetString()).IsEqualTo("simulation.handshake");
        var parameters = root.GetProperty("params");
        await Assert.That(parameters.GetProperty("expectedRole").GetString()).IsEqualTo("backend");
        await Assert.That(parameters.GetProperty("offeredVersions")[0].GetInt32()).IsEqualTo(1);
        var required = parameters.GetProperty("requiredCapabilities").EnumerateArray()
            .Select(static capability => capability.GetString()).ToArray();
        await Assert.That(required).Contains("llm.attach");
        await Assert.That(required).Contains("llm.request");
        await Assert.That(required).Contains("llm.chunk");
        await Assert.That(required).Contains("llm.finish");
    }

    [Test]
    public async Task Attach_Should_Carry_No_Params()
    {
        using var message = Parse(DriveProtocol.Attach(id: 2));

        await Assert.That(message.RootElement.GetProperty("method").GetString()).IsEqualTo("llm.attach");
        await Assert.That(message.RootElement.TryGetProperty("params", out _)).IsFalse();
    }

    [Test]
    public async Task ChunkText_Should_Script_Text_Deltas_For_The_Invocation()
    {
        using var message = Parse(DriveProtocol.ChunkText(3, "inv_1", ["Hello ", "drive"]));
        var parameters = message.RootElement.GetProperty("params");

        await Assert.That(message.RootElement.GetProperty("method").GetString()).IsEqualTo("llm.chunk");
        await Assert.That(parameters.GetProperty("id").GetString()).IsEqualTo("inv_1");
        var items = parameters.GetProperty("items");
        await Assert.That(items.GetArrayLength()).IsEqualTo(2);
        await Assert.That(items[0].GetProperty("type").GetString()).IsEqualTo("textDelta");
        await Assert.That(items[0].GetProperty("text").GetString()).IsEqualTo("Hello ");
        await Assert.That(items[1].GetProperty("text").GetString()).IsEqualTo("drive");
    }

    [Test]
    public async Task Finish_Should_Name_The_Invocation_And_Reason()
    {
        using var message = Parse(DriveProtocol.Finish(4, "inv_1", "stop"));
        var parameters = message.RootElement.GetProperty("params");

        await Assert.That(message.RootElement.GetProperty("method").GetString()).IsEqualTo("llm.finish");
        await Assert.That(parameters.GetProperty("id").GetString()).IsEqualTo("inv_1");
        await Assert.That(parameters.GetProperty("reason").GetString()).IsEqualTo("stop");
    }

    [Test]
    public async Task Disconnect_Should_Name_The_Invocation()
    {
        using var message = Parse(DriveProtocol.Disconnect(5, "inv_1"));

        await Assert.That(message.RootElement.GetProperty("method").GetString()).IsEqualTo("llm.disconnect");
        await Assert.That(message.RootElement.GetProperty("params").GetProperty("id").GetString())
            .IsEqualTo("inv_1");
    }

    [Test]
    public async Task Pending_Should_Carry_No_Params()
    {
        using var message = Parse(DriveProtocol.Pending(id: 6));

        await Assert.That(message.RootElement.GetProperty("method").GetString()).IsEqualTo("llm.pending");
        await Assert.That(message.RootElement.TryGetProperty("params", out _)).IsFalse();
    }
}
