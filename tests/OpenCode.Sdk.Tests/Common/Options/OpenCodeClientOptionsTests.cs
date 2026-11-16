namespace OpenCode.Sdk.Tests.Common.Options;

public sealed class OpenCodeClientOptionsTests
{
    [Test]
    public async Task HttpHandler_Should_Be_A_Nullable_Settable_Handler_Defaulting_To_Null()
    {
        var property = typeof(OpenCodeClientOptions).GetProperty("HttpHandler");
        await Assert.That(property).IsNotNull();
        await Assert.That(property!.PropertyType).IsEqualTo(typeof(HttpMessageHandler));
        await Assert.That(property.CanWrite).IsTrue();
        var options = new OpenCodeClientOptions();
        await Assert.That(property.GetValue(options)).IsNull();
        using var handler = new HttpClientHandler();
        property.SetValue(options, handler);
        await Assert.That(property.GetValue(options)).IsSameReferenceAs(handler);
    }

    [Test]
    public async Task Headers_Should_Be_An_Empty_Case_Insensitive_GetOnly_Collection()
    {
        var property = typeof(OpenCodeClientOptions).GetProperty("Headers");
        await Assert.That(property).IsNotNull();
        await Assert.That(property!.CanWrite).IsFalse();
        var headers = (IDictionary<string, string>)property.GetValue(new OpenCodeClientOptions())!;
        await Assert.That(headers).IsEmpty();
        headers.Add("X-Probe", "first");
        headers["x-probe"] = "second";
        await Assert.That(headers.Count).IsEqualTo(1);
        await Assert.That(headers["X-PROBE"]).IsEqualTo("second");
    }

    [Test]
    public async Task OpenCodeClientOptions_Should_Default_To_The_Opencode_Username()
    {
        var options = new OpenCodeClientOptions();

        await Assert.That(options.Username).IsEqualTo("opencode");
        await Assert.That(options.Endpoint).IsNull();
        await Assert.That(options.Password).IsNull();
    }
}
