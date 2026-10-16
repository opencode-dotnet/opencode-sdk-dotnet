using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenCode.Sdk;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers missing opencode client services as singletons. The default
/// <see cref="OpenCodeClient"/> owns its transport for the container's lifetime, and default
/// sub-clients resolve from the registered root. Earlier registrations of the root or any
/// sub-client are preserved.
/// </summary>
public static class OpenCodeServiceCollectionExtensions
{
    /// <summary>Registers the opencode client, shaping its options in code.</summary>
    /// <param name="services">The service collection to register with.</param>
    /// <param name="configure">Shapes the client options; the endpoint is required.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddOpenCode(this IServiceCollection services, Action<OpenCodeClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        _ = services.Configure(configure);
        return AddOpenCodeCore(services);
    }

    /// <summary>Registers the opencode client, binding its options from configuration.</summary>
    /// <param name="services">The service collection to register with.</param>
    /// <param name="configuration">The configuration section carrying the client options.</param>
    /// <returns>The service collection for chaining.</returns>
    [RequiresDynamicCode("Configuration binding uses reflection over the options type; prefer the configure-action overload on native AOT.")]
    [RequiresUnreferencedCode("Configuration binding may require members trimming removes; prefer the configure-action overload when trimming.")]
    public static IServiceCollection AddOpenCode(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        _ = services.AddOptions<OpenCodeClientOptions>().Bind(configuration);
        return AddOpenCodeCore(services);
    }

    private static IServiceCollection AddOpenCodeCore(IServiceCollection services)
    {
        // Register only missing services so earlier consumer registrations win.
        // The default root owns the transport; default sub-clients resolve from the
        // registered root. Repeated calls add no client registrations, while options
        // configuration still follows the standard options pattern.
        services.TryAddSingleton(static provider => new OpenCodeClient(provider.GetRequiredService<IOptions<OpenCodeClientOptions>>().Value));
        services.TryAddSingleton(static AgentsClient (provider) => provider.GetRequiredService<OpenCodeClient>().Agents);
        services.TryAddSingleton(static CommandsClient (provider) => provider.GetRequiredService<OpenCodeClient>().Commands);
        services.TryAddSingleton(static ConfigClient (provider) => provider.GetRequiredService<OpenCodeClient>().Config);
        services.TryAddSingleton(static CredentialsClient (provider) => provider.GetRequiredService<OpenCodeClient>().Credentials);
        services.TryAddSingleton(static DebugClient (provider) => provider.GetRequiredService<OpenCodeClient>().Debug);
        services.TryAddSingleton(static EventsClient (provider) => provider.GetRequiredService<OpenCodeClient>().Events);
        services.TryAddSingleton(static ExperimentalClient (provider) => provider.GetRequiredService<OpenCodeClient>().Experimental);
        services.TryAddSingleton(static FileSystemClient (provider) => provider.GetRequiredService<OpenCodeClient>().FileSystem);
        services.TryAddSingleton(static FormsClient (provider) => provider.GetRequiredService<OpenCodeClient>().Forms);
        services.TryAddSingleton(static IntegrationsClient (provider) => provider.GetRequiredService<OpenCodeClient>().Integrations);
        services.TryAddSingleton(static LanguageModelsClient (provider) => provider.GetRequiredService<OpenCodeClient>().LanguageModels);
        services.TryAddSingleton(static McpServersClient (provider) => provider.GetRequiredService<OpenCodeClient>().McpServers);
        services.TryAddSingleton(static PermissionsClient (provider) => provider.GetRequiredService<OpenCodeClient>().Permissions);
        services.TryAddSingleton(static PersistentPtysClient (provider) => provider.GetRequiredService<OpenCodeClient>().PersistentPtys);
        services.TryAddSingleton(static PluginsClient (provider) => provider.GetRequiredService<OpenCodeClient>().Plugins);
        services.TryAddSingleton(static ProjectsClient (provider) => provider.GetRequiredService<OpenCodeClient>().Projects);
        services.TryAddSingleton(static ProvidersClient (provider) => provider.GetRequiredService<OpenCodeClient>().Providers);
        services.TryAddSingleton(static PtysClient (provider) => provider.GetRequiredService<OpenCodeClient>().Ptys);
        services.TryAddSingleton(static ReferencesClient (provider) => provider.GetRequiredService<OpenCodeClient>().References);
        services.TryAddSingleton(static RpcClient (provider) => provider.GetRequiredService<OpenCodeClient>().Rpc);
        services.TryAddSingleton(static ServerClient (provider) => provider.GetRequiredService<OpenCodeClient>().Server);
        services.TryAddSingleton(static SessionsClient (provider) => provider.GetRequiredService<OpenCodeClient>().Sessions);
        services.TryAddSingleton(static ShellsClient (provider) => provider.GetRequiredService<OpenCodeClient>().Shells);
        services.TryAddSingleton(static SkillsClient (provider) => provider.GetRequiredService<OpenCodeClient>().Skills);
        services.TryAddSingleton(static VcsClient (provider) => provider.GetRequiredService<OpenCodeClient>().Vcs);
        services.TryAddSingleton(static WebsearchClient (provider) => provider.GetRequiredService<OpenCodeClient>().Websearch);
        services.TryAddSingleton(static WorktreesClient (provider) => provider.GetRequiredService<OpenCodeClient>().Worktrees);
        return services;
    }
}
