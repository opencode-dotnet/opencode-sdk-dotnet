using System.IO.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenCode.Sdk.Tools.Commands;
using OpenCode.Sdk.Tools.Generator;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Infrastructure;
using OpenCode.Sdk.Tools.Infrastructure.Logging;
using OpenCode.Sdk.Tools.Output.Abstractions;
using OpenCode.Sdk.Tools.Tests.Support;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Cli.Testing;
using Spectre.Console.Testing;
using Testably.Abstractions;
using Testably.Abstractions.Testing;

namespace OpenCode.Sdk.Tools.Tests;

public sealed class ToolAppTests
{
    [Test]
    public async Task CreateServices_Should_Resolve_Full_Hosting_Composition()
    {
        await using var provider = ToolApp.CreateServices().BuildServiceProvider();

        var fileSystem = provider.GetRequiredService<IFileSystem>();
        var console = provider.GetRequiredService<IAnsiConsole>();
        var logger = provider.GetRequiredService<ILogger<GenerateCommand>>();
        var loggingOptions = provider.GetRequiredService<ToolLoggingOptions>();
        var coordinator = provider.GetRequiredService<GenerationCoordinator>();
        var writer = provider.GetRequiredService<IGenerationWriter>();
        var formatter = provider.GetRequiredService<IProjectFormatter>();
        var interceptors = provider.GetServices<ICommandInterceptor>().ToArray();

        await Assert.That(fileSystem).IsTypeOf<RealFileSystem>();
        await Assert.That(console).IsSameReferenceAs(AnsiConsole.Console);
        await Assert.That(logger).IsNotNull();
        await Assert.That(loggingOptions).IsNotNull();
        await Assert.That(coordinator).IsNotNull();
        await Assert.That(writer).IsNotNull();
        await Assert.That(formatter).IsNotNull();
        await Assert.That(interceptors.Length).IsEqualTo(1);
        await Assert.That(interceptors[0]).IsTypeOf<GlobalOptionsInterceptor>();
    }

    [Test]
    public async Task CreateRegistrar_Should_Apply_Seam_Overrides_After_Production_Registrations()
    {
        var expected = new MockFileSystem();
        using var registrar = ToolApp.CreateRegistrar(services => services.AddSingleton<IFileSystem>(expected));
        var resolver = registrar.Build();

        var actual = resolver.Resolve(typeof(IFileSystem));

        await Assert.That(actual).IsSameReferenceAs(expected);
    }

    [Test]
    public async Task RunAsync_Should_Apply_Global_Logging_Options_Before_Command_Execution()
    {
        const string logPath = "logs/tool.log";
        var fileSystem = GenerationTestData.CreateCommandFileSystem();
        using var registrar = ToolApp.CreateRegistrar(services =>
        {
            services.AddSingleton<IFileSystem>(fileSystem);
            services.AddSingleton<IAnsiConsole, TestConsole>();
            services.AddSingleton<IProjectFormatter>(new RecordingProjectFormatter(fileSystem));
        });
        var tester = new CommandAppTester(registrar);
        tester.Configure(ToolApp.Configure);

        var result = await tester.RunAsync(["generate", "--log-level", "debug", "--log-file", logPath]);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(fileSystem.File.Exists(logPath)).IsTrue();
        var log = await fileSystem.File.ReadAllTextAsync(logPath, CancellationToken.None);
        await Assert.That(log).Contains("Generate command invoked.");
    }

    [Test]
    public async Task RunAsync_Should_Return_Nonzero_When_Command_Is_Unknown()
    {
        using var registrar = ToolApp.CreateRegistrar();
        var tester = new CommandAppTester(registrar);
        tester.Configure(ToolApp.Configure);

        var result = await tester.RunAsync(["does-not-exist"]);

        await Assert.That(result.ExitCode).IsNotEqualTo(0);
    }

    [Test]
    public async Task RunAsync_Should_Generate_Selected_Output()
    {
        var fileSystem = GenerationTestData.CreateCommandFileSystem();
        using var registrar = ToolApp.CreateRegistrar(services =>
        {
            services.AddSingleton<IFileSystem>(fileSystem);
            services.AddSingleton<IAnsiConsole, TestConsole>();
            services.AddSingleton<IProjectFormatter>(new RecordingProjectFormatter(fileSystem));
        });
        var tester = new CommandAppTester(registrar);
        tester.Configure(ToolApp.Configure);

        var result = await tester.RunAsync(["generate"]);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(fileSystem.File.Exists(GenerationTestData.ManifestPath)).IsTrue();
        await Assert.That(fileSystem.File.Exists(GenerationTestData.MarkerPath)).IsTrue();
        await Assert.That(result.Output).Contains("Pending operations");
    }

    [Test]
    public async Task RunAsync_Should_Mark_Pending_Operations_With_Their_Bindability()
    {
        var fileSystem = GenerationTestData.CreateCommandFileSystem();
        using var registrar = ToolApp.CreateRegistrar(services =>
        {
            services.AddSingleton<IFileSystem>(fileSystem);
            services.AddSingleton<IAnsiConsole, TestConsole>();
            services.AddSingleton<IProjectFormatter>(new RecordingProjectFormatter(fileSystem));
        });
        var tester = new CommandAppTester(registrar);
        tester.Configure(ToolApp.Configure);

        var result = await tester.RunAsync(["generate"]);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        var marker = await fileSystem.File.ReadAllTextAsync(GenerationTestData.MarkerPath, CancellationToken.None);
        await Assert.That(marker).Contains("- plugin.list [bindable]");
        await Assert.That(marker).Contains("- session.list [refused: the success response must carry a JSON schema]");
        await Assert.That(marker).Contains(
            "- widget.tail [refused: WebSocket operations are not supported in M1; PUT operations must carry a request body]");
    }

    [Test]
    public async Task RunAsync_Should_List_Transport_Owned_Operations_Beside_The_Pending_Map()
    {
        var fileSystem = await GenerationTestData.CreateTransportOwnedCommandFileSystemAsync();
        using var registrar = ToolApp.CreateRegistrar(services =>
        {
            services.AddSingleton<IFileSystem>(fileSystem);
            services.AddSingleton<IAnsiConsole, TestConsole>();
            services.AddSingleton<IProjectFormatter>(new RecordingProjectFormatter(fileSystem));
        });
        var tester = new CommandAppTester(registrar);
        tester.Configure(ToolApp.Configure);

        var result = await tester.RunAsync(["generate"]);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Output).Contains("Transport-owned operations: 1");
        var marker = await fileSystem.File.ReadAllTextAsync(GenerationTestData.MarkerPath, CancellationToken.None);
        await Assert.That(marker).Contains("Pending operations: 3\nDeclined operations: 0\nTransport-owned operations: 1\n");
        await Assert.That(marker).Contains("- plugin.list [bindable]");
        await Assert.That(marker).Contains("Transport-owned:\n- pty.connect [fingerprint-pinned]\n");
        await Assert.That(marker).DoesNotContain("- pty.connect [refused");
    }

    [Test]
    public async Task RunAsync_Should_List_Declined_Operations_With_Their_Reason_And_Walls()
    {
        var fileSystem = GenerationTestData.CreateDeclinedCommandFileSystem();
        using var registrar = ToolApp.CreateRegistrar(services =>
        {
            services.AddSingleton<IFileSystem>(fileSystem);
            services.AddSingleton<IAnsiConsole, TestConsole>();
            services.AddSingleton<IProjectFormatter>(new RecordingProjectFormatter(fileSystem));
        });
        var tester = new CommandAppTester(registrar);
        tester.Configure(ToolApp.Configure);

        var result = await tester.RunAsync(["generate"]);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Output).Contains("Declined operations: 1");
        var marker = await fileSystem.File.ReadAllTextAsync(GenerationTestData.MarkerPath, CancellationToken.None);
        await Assert.That(marker).Contains("Pending operations: 2\nDeclined operations: 1\nTransport-owned operations: 0\n");
        // The declined line carries the decision and the wall the binder finds today, so the two
        // are reviewed against each other in one place.
        await Assert.That(marker).Contains(
            "Declined:\n- widget.tail [declined: The operation is WebSocket-marked and a bodyless PUT, "
            + "so it does not bind; maintainer 2026-08-30.] "
            + "[refused: WebSocket operations are not supported in M1; PUT operations must carry a request body]\n");
        await Assert.That(marker).DoesNotContain("Pending:\n- widget.tail");
    }

    [Test]
    public async Task RunAsync_Should_Keep_The_Marker_When_Every_Gap_Is_Declined()
    {
        var fileSystem = GenerationTestData.CreateFullyDeclinedCommandFileSystem();
        using var registrar = ToolApp.CreateRegistrar(services =>
        {
            services.AddSingleton<IFileSystem>(fileSystem);
            services.AddSingleton<IAnsiConsole, TestConsole>();
            services.AddSingleton<IProjectFormatter>(new RecordingProjectFormatter(fileSystem));
        });
        var tester = new CommandAppTester(registrar);
        tester.Configure(ToolApp.Configure);

        var result = await tester.RunAsync(["generate"]);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        var marker = await fileSystem.File.ReadAllTextAsync(GenerationTestData.MarkerPath, CancellationToken.None);
        // The marker outlives a cleared pending set: it is the committed record of what the
        // released surface omits, and the packing wall reads its pending count, not its existence.
        await Assert.That(marker).StartsWith("Generation is complete at the declared coverage; packages may be published.\n");
        await Assert.That(marker).Contains("Pending operations: 0\nDeclined operations: 2\nTransport-owned operations: 0\n");
        await Assert.That(marker).Contains("Pending:\nDeclined:\n- session.list [declined: ");
    }

    [Test]
    public async Task RunAsync_Should_Refuse_A_Declined_Row_Over_A_Bindable_Operation()
    {
        var fileSystem = GenerationTestData.CreateBindableDeclineCommandFileSystem();
        using var registrar = ToolApp.CreateRegistrar(services =>
        {
            services.AddSingleton<IFileSystem>(fileSystem);
            services.AddSingleton<IAnsiConsole, TestConsole>();
            services.AddSingleton<IProjectFormatter>(new RecordingProjectFormatter(fileSystem));
        });
        var tester = new CommandAppTester(registrar);
        // The refusal is the binder's own exception; propagating it keeps the assertion on the
        // refusal rather than on Spectre's rendered crash output.
        tester.Configure(configurator =>
        {
            ToolApp.Configure(configurator);
            configurator.PropagateExceptions();
        });

        // Decline is only for walled operations: an operation that binds today has to be selected
        // or left pending, so the row cannot outlive the wall it cites.
        var exception = await Assert
            .That(async () => _ = await tester.RunAsync(["generate"]))
            .Throws<BindingException>();

        var error = exception!.Errors.Single();
        await Assert.That(error.Category).IsEqualTo(BindingErrorCategory.Curation);
        await Assert.That(error.Subject).IsEqualTo("plugin.list");
        await Assert.That(error.Problem).Contains("a bindable operation cannot be declined");
        await Assert.That(fileSystem.File.Exists(GenerationTestData.MarkerPath)).IsFalse();
    }

    [Test]
    public async Task RunAsync_Should_Refuse_A_Model_With_No_Family_Prefix_And_No_Owner_Row()
    {
        var fileSystem = GenerationTestData.CreateUnplacedModelCommandFileSystem();
        using var registrar = ToolApp.CreateRegistrar(services =>
        {
            services.AddSingleton<IFileSystem>(fileSystem);
            services.AddSingleton<IAnsiConsole, TestConsole>();
            services.AddSingleton<IProjectFormatter>(new RecordingProjectFormatter(fileSystem));
        });
        var tester = new CommandAppTester(registrar);
        tester.Configure(configurator =>
        {
            ToolApp.Configure(configurator);
            configurator.PropagateExceptions();
        });

        // ExampleHealth matches no prefix of its Health family and no row names its owner, so the
        // model has no declared slice and generation refuses before writing anything.
        var exception = await Assert
            .That(async () => _ = await tester.RunAsync(["generate"]))
            .Throws<BindingException>();

        var error = exception!.Errors.Single();
        await Assert.That(error.Category).IsEqualTo(BindingErrorCategory.Curation);
        await Assert.That(error.Subject).IsEqualTo("ExampleHealth");
        await Assert.That(error.Problem).Contains("the model has no family prefix and no modelOwners row");
        await Assert.That(fileSystem.File.Exists(GenerationTestData.ManifestPath)).IsFalse();
    }

    [Test]
    public async Task RunAsync_Should_Return_Zero_When_Verify_Is_Clean()
    {
        var fileSystem = GenerationTestData.CreateCommandFileSystem();
        using var registrar = ToolApp.CreateRegistrar(services =>
        {
            services.AddSingleton<IFileSystem>(fileSystem);
            services.AddSingleton<IAnsiConsole, TestConsole>();
            services.AddSingleton<IProjectFormatter>(new RecordingProjectFormatter(fileSystem));
        });
        var generateTester = new CommandAppTester(registrar);
        generateTester.Configure(ToolApp.Configure);
        _ = await generateTester.RunAsync(["generate"]);

        var verifyTester = new CommandAppTester(registrar);
        verifyTester.Configure(ToolApp.Configure);
        var result = await verifyTester.RunAsync(["generate", "--verify"]);

        await Assert.That(result.ExitCode).IsEqualTo(0);
    }

    [Test]
    public async Task RunAsync_Should_Return_Nonzero_And_Report_Verify_Drift()
    {
        const string generatedPath = "src/OpenCode.Sdk/Internal/Serialization/OpenCodeJsonContext.cs";
        var fileSystem = GenerationTestData.CreateCommandFileSystem();
        using var registrar = ToolApp.CreateRegistrar(services =>
        {
            services.AddSingleton<IFileSystem>(fileSystem);
            services.AddSingleton<IAnsiConsole, TestConsole>();
            services.AddSingleton<IProjectFormatter>(new RecordingProjectFormatter(fileSystem));
        });
        var generateTester = new CommandAppTester(registrar);
        generateTester.Configure(ToolApp.Configure);
        _ = await generateTester.RunAsync(["generate"]);
        await fileSystem.File.WriteAllTextAsync(generatedPath, GenerationTestData.OwnedContent("drift"), CancellationToken.None);

        var verifyTester = new CommandAppTester(registrar);
        verifyTester.Configure(ToolApp.Configure);
        var result = await verifyTester.RunAsync(["generate", "--verify"]);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Output).Contains("Internal/Serialization/OpenCodeJsonContext.cs");
    }
}
