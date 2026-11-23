using OpenCode.Playground.Hq.Features.Conversation;

namespace OpenCode.Playground.Hq.Tests.Features.Conversation;

public sealed class SessionScreenTests
{
    [Test]
    public async Task Rendered_Should_Escape_Raw_Html_And_Render_Markdown()
    {
        var html = SessionScreen.Rendered("<script>alert(1)</script>\n\n<img onerror=\"alert(2)\">\n\n### Heading\n\n- item with `code`").Value;

        await Assert.That(html).Contains("&lt;script&gt;alert(1)&lt;/script&gt;");
        await Assert.That(html).Contains("&lt;img onerror=&quot;alert(2)&quot;&gt;");
        await Assert.That(html).Contains("<h3>Heading</h3>");
        await Assert.That(html).Contains("<ul>\n<li>item with <code>code</code></li>\n</ul>");
    }
}
