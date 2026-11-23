using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenCode.Playground.Hq.Features.Panel;

namespace OpenCode.Playground.Hq.Tests.Features.Panel;

public sealed class SdkPanelTests
{
    [Test]
    public async Task SdkPanel_Should_Respond_To_The_Circuits_Toggle_With_Numbered_Source()
    {
        var panel = new PanelState();
        await using var services = new ServiceCollection().AddLogging().AddSingleton(panel).AddSingleton<SourceSnippets>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<SdkPanel>(ParameterView.FromDictionary(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [nameof(SdkPanel.Entries)] = PanelEntry.FirstScreen,
            }));
            await Assert.That(component.ToHtmlString()).DoesNotContain("sdk-dock");
            panel.Toggle();

            var html = component.ToHtmlString();
            await Assert.That(html).Contains("id=\"sdk-dock\"");
            await Assert.That(html).Contains("Read the streaming guide");
            await Assert.That(html).Contains("#L");
            await Assert.That(html).Contains("class=\"ln\"");
            await Assert.That(html).Contains("ReplyToFormAsync");
            panel.Toggle();
            await Assert.That(component.ToHtmlString()).DoesNotContain("sdk-dock");
        });
    }
}
