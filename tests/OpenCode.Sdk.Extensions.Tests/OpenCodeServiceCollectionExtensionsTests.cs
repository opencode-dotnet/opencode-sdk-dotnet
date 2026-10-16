using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenCode.Sdk.Extensions.Tests.Support;

namespace OpenCode.Sdk.Extensions.Tests;

public sealed class OpenCodeServiceCollectionExtensionsTests
{
    private static readonly Uri Endpoint = new("http://localhost:4096");

    [Test]
    public async Task AddOpenCode_Should_Resolve_One_Singleton_Client()
    {
        using var provider = BuildProvider();

        var first = provider.GetRequiredService<OpenCodeClient>();
        var second = provider.GetRequiredService<OpenCodeClient>();

        await Assert.That(first).IsSameReferenceAs(second);
    }

    [Test]
    public async Task AddOpenCode_Should_Resolve_The_Sessions_Client_From_The_Same_Root_Instance()
    {
        using var provider = BuildProvider();

        var root = provider.GetRequiredService<OpenCodeClient>();
        var sessions = provider.GetRequiredService<SessionsClient>();

        await Assert.That(sessions).IsSameReferenceAs(root.Sessions);
    }

    [Test]
    public async Task AddOpenCode_Should_Register_Every_Root_Client_Family()
    {
        using var provider = BuildProvider();
        var root = provider.GetRequiredService<OpenCodeClient>();
        var families = typeof(OpenCodeClient).GetProperties()
            .Where(property => property.PropertyType.Name.EndsWith("Client", StringComparison.Ordinal))
            .ToList();

        await Assert.That(families).IsNotEmpty();
        foreach (var family in families)
        {
            var resolved = provider.GetService(family.PropertyType);
            await Assert.That(resolved).IsNotNull();
            await Assert.That(resolved).IsSameReferenceAs(family.GetValue(root));
        }
    }

    [Test]
    public async Task AddOpenCode_Should_Apply_The_Configure_Action_When_Building_The_Client()
    {
        var configured = new List<OpenCodeClientOptions>();
        var services = new ServiceCollection();
        _ = services.AddOpenCode(options =>
        {
            options.Endpoint = Endpoint;
            configured.Add(options);
        });
        using var provider = services.BuildServiceProvider();

        _ = provider.GetRequiredService<OpenCodeClient>();

        await Assert.That(configured).HasSingleItem();
    }

    [Test]
    public async Task AddOpenCode_Should_Bind_The_Configuration_Section_Including_Credentials()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(OpenCodeConfigurationData.ProtectedServer)
            .Build();
        var services = new ServiceCollection();
        _ = services.AddOpenCode(configuration.GetSection("OpenCode"));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<OpenCodeClientOptions>>().Value;

        await Assert.That(options.Endpoint).IsEqualTo(Endpoint);
        await Assert.That(options.Username).IsEqualTo("admin");
        await Assert.That(options.Password).IsEqualTo("secret");
    }

    [Test]
    public async Task AddOpenCode_Should_Bind_The_Event_Stream_Idle_Window_From_Configuration()
    {
        using var provider = Bind(OpenCodeConfigurationData.WithEventStreamIdleTimeout("00:00:23"));

        var options = provider.GetRequiredService<IOptions<OpenCodeClientOptions>>().Value;

        await Assert.That(options.EventStreamIdleTimeout).IsEqualTo(TimeSpan.FromSeconds(23));
    }

    /// <summary>The setter's refusal reaches configuration too: binding a window that is empty or never ends fails rather than leaving the default.</summary>
    [Test]
    [Arguments("00:00:00")]
    [Arguments("-00:00:00.001")]
    public async Task AddOpenCode_Should_Refuse_An_Idle_Window_The_Option_Refuses(string value)
    {
        using var provider = Bind(OpenCodeConfigurationData.WithEventStreamIdleTimeout(value));

        var failure = await Assert.That(() => provider.GetRequiredService<IOptions<OpenCodeClientOptions>>().Value).Throws<Exception>();

        await Assert.That(Chain(failure!).OfType<ArgumentOutOfRangeException>().Select(static refusal => refusal.ParamName ?? string.Empty))
            .IsEquivalentTo(["value"]);
    }

    [Test]
    public async Task AddOpenCode_Should_Return_The_Service_Collection_For_Chaining()
    {
        var services = new ServiceCollection();

        var returned = services.AddOpenCode(static options => options.Endpoint = Endpoint);

        await Assert.That(returned).IsSameReferenceAs(services);
    }

    [Test]
    public async Task AddOpenCode_Should_Refuse_A_Null_Service_Collection()
    {
        _ = await Assert.That(() => ((IServiceCollection)null!).AddOpenCode(static _ => { }))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task AddOpenCode_Should_Refuse_A_Null_Configure_Action()
    {
        _ = await Assert.That(() => new ServiceCollection().AddOpenCode(configure: null!))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task AddOpenCode_Should_Refuse_A_Null_Configuration()
    {
        _ = await Assert.That(() => new ServiceCollection().AddOpenCode(configuration: null!))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task AddOpenCode_Should_Register_One_Root_And_Each_Family_Once_When_Called_Twice()
    {
        var services = new ServiceCollection();
        _ = services.AddOpenCode(static options => options.Endpoint = Endpoint);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(OpenCodeConfigurationData.ProtectedServer)
            .Build();
        _ = services.AddOpenCode(configuration.GetSection("OpenCode"));
        using var provider = services.BuildServiceProvider();

        var roots = provider.GetServices<OpenCodeClient>().ToList();
        await Assert.That(roots).HasSingleItem();
        var root = provider.GetRequiredService<OpenCodeClient>();
        await Assert.That(roots[0]).IsSameReferenceAs(root);

        var families = typeof(OpenCodeClient).GetProperties()
            .Where(property => property.PropertyType.Name.EndsWith("Client", StringComparison.Ordinal))
            .ToList();
        foreach (var family in families)
        {
            var resolved = provider.GetServices(family.PropertyType).ToList();
            await Assert.That(resolved).HasSingleItem();
            await Assert.That(resolved[0]).IsSameReferenceAs(family.GetValue(root));
        }
    }

    [Test]
    public async Task AddOpenCode_Should_Preserve_An_Earlier_Root_Client_Registration()
    {
        using var consumerClient = new OpenCodeClient(new OpenCodeClientOptions { Endpoint = Endpoint });
        var services = new ServiceCollection();
        _ = services.AddSingleton(consumerClient);
        _ = services.AddOpenCode(static options => options.Endpoint = Endpoint);
        using var provider = services.BuildServiceProvider();

        await Assert.That(provider.GetRequiredService<OpenCodeClient>()).IsSameReferenceAs(consumerClient);
        await Assert.That(provider.GetServices<OpenCodeClient>().ToList()).HasSingleItem();
        await Assert.That(provider.GetRequiredService<SessionsClient>()).IsSameReferenceAs(consumerClient.Sessions);
    }

    [Test]
    public async Task AddOpenCode_Should_Preserve_An_Earlier_Sub_Client_Registration()
    {
        using var consumerClient = new OpenCodeClient(new OpenCodeClientOptions { Endpoint = Endpoint });
        var consumerSessions = consumerClient.Sessions;
        var services = new ServiceCollection();
        _ = services.AddSingleton(consumerSessions);
        _ = services.AddOpenCode(static options => options.Endpoint = Endpoint);
        using var provider = services.BuildServiceProvider();

        var sessions = provider.GetRequiredService<SessionsClient>();
        await Assert.That(sessions).IsSameReferenceAs(consumerSessions);
        await Assert.That(provider.GetServices<SessionsClient>().ToList()).HasSingleItem();
        await Assert.That(sessions).IsNotSameReferenceAs(provider.GetRequiredService<OpenCodeClient>().Sessions);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        _ = services.AddOpenCode(static options => options.Endpoint = Endpoint);
        return services.BuildServiceProvider();
    }

    private static ServiceProvider Bind(IReadOnlyDictionary<string, string?> section)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(section).Build();
        var services = new ServiceCollection();
        _ = services.AddOpenCode(configuration.GetSection("OpenCode"));
        return services.BuildServiceProvider();
    }

    /// <summary>The failure and every exception it wraps, outermost first.</summary>
    private static IEnumerable<Exception> Chain(Exception failure)
    {
        for (var current = failure; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }
}
