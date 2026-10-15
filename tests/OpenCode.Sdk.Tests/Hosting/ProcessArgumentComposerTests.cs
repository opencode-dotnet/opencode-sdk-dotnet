using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Hosting;

public sealed class ProcessArgumentComposerTests
{
    [Test]
    public async Task Compose_Should_Pass_Plain_Arguments_Through()
    {
        var composed = ProcessArgumentComposer.Compose(["serve", "--stdio", "--port", "0"]);

        await Assert.That(composed).IsEqualTo("serve --stdio --port 0");
    }

    [Test]
    public async Task Compose_Should_Quote_An_Argument_With_Spaces()
    {
        var composed = ProcessArgumentComposer.Compose([@"C:\tools with spaces\index.ts"]);

        await Assert.That(composed).IsEqualTo(@"""C:\tools with spaces\index.ts""");
    }

    [Test]
    public async Task Compose_Should_Escape_An_Embedded_Quote()
    {
        var composed = ProcessArgumentComposer.Compose(["say \"hi\""]);

        await Assert.That(composed).IsEqualTo("\"say \\\"hi\\\"\"");
    }

    [Test]
    public async Task Compose_Should_Double_Trailing_Backslashes_Inside_Quotes()
    {
        var composed = ProcessArgumentComposer.Compose(["path ends\\"]);

        await Assert.That(composed).IsEqualTo("\"path ends\\\\\"");
    }

    [Test]
    public async Task Compose_Should_Quote_An_Empty_Argument()
    {
        var composed = ProcessArgumentComposer.Compose([string.Empty]);

        await Assert.That(composed).IsEqualTo("\"\"");
    }
}
