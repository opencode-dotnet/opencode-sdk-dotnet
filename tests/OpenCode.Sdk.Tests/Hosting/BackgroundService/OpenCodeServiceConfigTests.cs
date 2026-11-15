using System.Reflection;
using OpenCode.Sdk.Tests.Hosting.BackgroundService.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService;

public sealed class OpenCodeServiceConfigTests
{
    [Test]
    public async Task OpenCodeServiceConfig_Should_Expose_Only_The_Named_Properties_Without_Secrets()
    {
        var scenario = new ServiceConfigScenario();
        await scenario.SeedAsync(scenario.Paths.ConfigFile!, scenario.Initial("all-members")!);
        var config = await OpenCodeServiceConfiguration.ReadWithSeamsAsync(scenario.Options, scenario.Store, CancellationToken.None);
        var properties = typeof(OpenCodeServiceConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        // Resolve the actual rendering, including object.ToString, as the registration secrecy test does.
        var rendering = (string)typeof(OpenCodeServiceConfig).GetMethod(nameof(ToString), Type.EmptyTypes)!.Invoke(config, null)!;

        await Assert.That(properties.Select(static property => property.Name).OrderBy(static name => name, StringComparer.Ordinal))
            .IsEquivalentTo(["CorsOrigins", "Disabled", "Environment", "Hostname", "Port", "RemoteAccepted", "RemoteEnabled"], CollectionOrdering.Matching);
        foreach (var secret in new[] { "stored-password", "stored-route" })
        {
            await Assert.That(properties.Select(property => property.GetValue(config))).DoesNotContain(secret);
            await Assert.That(rendering).DoesNotContain(secret);
        }
    }
}
