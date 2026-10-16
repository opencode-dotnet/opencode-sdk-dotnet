using System.IO.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenCode.Sdk.Tools.Benchmarks;
using OpenCode.Sdk.Tools.Clones;
using OpenCode.Sdk.Tools.Commands;
using OpenCode.Sdk.Tools.Generator;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Abstractions;
using OpenCode.Sdk.Tools.Generator.Ingestion;
using OpenCode.Sdk.Tools.Generator.Ingestion.Abstractions;
using OpenCode.Sdk.Tools.Generator.Refresh;
using OpenCode.Sdk.Tools.Generator.Refresh.Abstractions;
using OpenCode.Sdk.Tools.Infrastructure;
using OpenCode.Sdk.Tools.Infrastructure.Logging;
using OpenCode.Sdk.Tools.Output;
using OpenCode.Sdk.Tools.Output.Abstractions;
using OpenCode.Sdk.Tools.TestRoster;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Cli.Extensions.DependencyInjection;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tools;

/// <summary>Composition root for the repo tool: DI registrar plus the command surface.</summary>
public static class ToolApp
{
    /// <summary>Builds the tool's complete production service collection.</summary>
    public static ServiceCollection CreateServices(Action<IServiceCollection>? overrideServices = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IFileSystem, RealFileSystem>();
        services.AddSingleton(AnsiConsole.Console);
        services.AddSingleton<ToolLoggingOptions>();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace));
        services.AddSingleton<ILoggerProvider, SpectreConsoleLoggerProvider>();
        services.AddSingleton<ILoggerProvider, FileLoggerProvider>();
        services.AddSingleton<ISpecIngestion, SpecIngestion>();
        services.AddSingleton<OperationSelectionLoader>();
        services.AddSingleton<CurationLoader>();
        services.AddSingleton<ReachableSchemaCollector>();
        services.AddSingleton<StabilizeDuplicatePolicy>();
        services.AddSingleton<CurationValidator>();
        services.AddSingleton<SchemaAliasValidator>();
        services.AddSingleton<SchemaAliasApplier>();
        services.AddSingleton<SchemaNameResolver>();
        services.AddSingleton<StructuralUnionPlanBinder>();
        services.AddSingleton<UnionMembershipValidator>();
        services.AddSingleton<UnionDiscriminatorSelector>();
        services.AddSingleton<UnionMemberHoistBinder>();
        services.AddSingleton<SchemaPlanBinder>();
        services.AddSingleton<OperationPlanBinder>();
        services.AddSingleton<ISpecBinder, SpecBinder>();
        services.AddSingleton<PendingOperationBindabilityProbe>();
        services.AddSingleton<IProjectFormatter, CliWrapProjectFormatter>();
        services.AddSingleton<IGenerationWriter, GenerationWriter>();
        services.AddSingleton<GenerationCoordinator>();
        services.AddSingleton<IProcessRunner, CliWrapProcessRunner>();
        services.AddSingleton<PatchSetLoader>();
        services.AddSingleton<SourceWatchLoader>();
        services.AddSingleton<WatchedSourceReader>();
        services.AddSingleton<SnapshotGenerator>();
        services.AddSingleton<SnapshotStateReader>();
        services.AddSingleton<SnapshotVerifier>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<SnapshotApplyPlanner>();
        services.AddSingleton<SnapshotApplyCommitter>();
        services.AddSingleton<SnapshotSynchronizer>();
        services.AddSingleton<BenchmarkRunReader>();
        services.AddSingleton<TestRosterLoader>();
        services.AddSingleton<TrxRunReader>();
        services.AddSingleton<CloneDetector>();
        services.AddSingleton<ICommandInterceptor, GlobalOptionsInterceptor>();

        overrideServices?.Invoke(services);
        return services;
    }

    /// <summary>Builds the DI registrar; tests inject service overrides.</summary>
    public static DependencyInjectionRegistrar CreateRegistrar(Action<IServiceCollection>? overrideServices = null)
    {
        return new DependencyInjectionRegistrar(CreateServices(overrideServices));
    }

    /// <summary>Registers the command surface; shared by the app and <c>CommandAppTester</c>.</summary>
    public static void Configure(IConfigurator configurator)
    {
        ArgumentNullException.ThrowIfNull(configurator);

        configurator.SetApplicationName("opencode-tool");
        configurator
            .AddCommand<GenerateCommand>("generate")
            .WithDescription("Regenerate the SDK model layer from spec/openapi.json.");
        configurator
            .AddCommand<CompareBenchmarksCommand>("compare-benchmarks")
            .WithDescription("Compare two BenchmarkDotNet run folders by exact allocated bytes and median time.");
        configurator
            .AddCommand<RefreshSpecCommand>("refresh-spec")
            .WithDescription("Prepare, verify, or apply a receipt-governed snapshot refresh (ADR-0020).");
        configurator
            .AddCommand<VerifyTestRosterCommand>("verify-test-roster")
            .WithDescription("Verify a CI test pass's TRX results against the committed tests/test-roster.json, fail-closed.");
        configurator
            .AddCommand<VerifyClonesCommand>("verify-clones")
            .WithDescription("Run the pinned clone detector: hand-written src/ and tools/ against their baselines, tests and generated output reported.");
    }

    /// <summary>Entry point used by tools/opencode-tool.cs.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        using var registrar = CreateRegistrar();
        var app = new CommandApp(registrar);
        app.Configure(Configure);
        return await app.RunAsync(args).ConfigureAwait(false);
    }
}
