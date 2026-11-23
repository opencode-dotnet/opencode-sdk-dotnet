using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenCode.Playground.Hq.Features.Files;
using TUnit.Assertions.Enums;

namespace OpenCode.Playground.Hq.Tests.Features.Files;

public sealed class DiffViewTests
{
    [Test]
    public async Task DiffView_Should_Keep_Headers_Meta_And_Colour_Only_Hunk_Lines()
    {
        var html = await RenderAsync("diff --git a/a b/a\n--- a/a\n+++ b/a\n@@ -1 +1 @@\n+new\n-old\n plain");

        await Assert.That(LineClasses(html)).IsEquivalentTo(["meta", "meta", "meta", "hunk", "add", "del", ""], CollectionOrdering.Matching);
        await Assert.That(html).Contains("aria-label=\"Patch for a\"");
    }

    [Test]
    public async Task DiffView_Should_Not_Show_The_Patch_Final_Newline_As_A_Line()
    {
        var html = await RenderAsync("@@ -0,0 +1,1 @@\n+only\n");

        await Assert.That(LineClasses(html)).IsEquivalentTo(["hunk", "add"], CollectionOrdering.Matching);
    }

    private static async Task<string> RenderAsync(string patch)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<DiffView>(ParameterView.FromDictionary(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [nameof(DiffView.Patch)] = patch,
                [nameof(DiffView.Label)] = "Patch for a",
            }));
            return component.ToHtmlString();
        });
    }

    private static IEnumerable<string> LineClasses(string html) =>
        html.Split("class=\"dl", StringSplitOptions.None).Skip(1)
            .Select(static line => line[..line.IndexOf('"', StringComparison.Ordinal)].Trim());
}
