using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using CliWrap;
using OpenCode.Sdk.Tools.Generator.Refresh;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions;
using TUnit.Core.Interfaces;

namespace OpenCode.Sdk.Tools.Tests.Packaging;

/// <summary>
/// The repository's packages, packed once per test session from the Release build the gate has
/// already run, into a feed of their own; and throwaway consumer projects built against them, the
/// way a consumer meets the published packages. Everything lives under one temporary root, which
/// stops MSBuild's upward search for build files and carries the repository's SDK pin, so a
/// consumer sees only what it is given. A consumer restores offline: its NuGet.Config clears every
/// inherited source and names only the feed and the machine's global packages folder, into a
/// packages folder of its own, so the packed versions never reach the global folder and a package
/// the build-time restore did not fetch fails the restore by name instead of reaching the network.
/// </summary>
public sealed class PackedConsumerFeed : IAsyncInitializer, IAsyncDisposable
{
    /// <summary>The prerelease suffix the packed versions carry, so no consumer can resolve a published one.</summary>
    private const string VersionSuffix = "packed-consumer";

    private const string DependencyInjection = "Microsoft.Extensions.DependencyInjection";

    private static readonly RealFileSystem FileSystem = new();

    private static readonly string[] PackedProjects =
    [
        "src/OpenCode.Sdk/OpenCode.Sdk.csproj",
        "src/OpenCode.Sdk.Extensions/OpenCode.Sdk.Extensions.csproj",
    ];

    private readonly CliWrapProcessRunner _runner = new();
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private string? _root;
    private string? _repository;
    private int _consumers;
    private ConsumerPublish? _published;

    /// <summary>Gets the version every packed package carries.</summary>
    public string Version { get; private set; } = string.Empty;

    /// <summary>Gets the directory the packed packages are in.</summary>
    public string FeedDirectory => FileSystem.Path.Combine(Root, "feed");

    private string Root => _root ?? throw new InvalidOperationException("The feed is not initialized.");

    private string Repository => _repository ?? throw new InvalidOperationException("The feed is not initialized.");

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        var repository = _repository = new RepositoryRoot(FileSystem).Locate();
        _root = FileSystem.Path.Combine(FileSystem.Path.GetTempPath(), "opencode-packed-consumer-" + Guid.NewGuid().ToString("N"));
        var feed = FileSystem.Path.Combine(_root, "feed");
        _ = FileSystem.Directory.CreateDirectory(feed);

        // The root's own build files end MSBuild's upward search here, and its global.json is the
        // repository's, so every consumer builds on the SDK the repository pins.
        await FileSystem.File.WriteAllTextAsync(FileSystem.Path.Combine(_root, "Directory.Build.props"), "<Project />").ConfigureAwait(false);
        await FileSystem.File.WriteAllTextAsync(FileSystem.Path.Combine(_root, "Directory.Build.targets"), "<Project />").ConfigureAwait(false);
        FileSystem.File.Copy(FileSystem.Path.Combine(repository, "global.json"), FileSystem.Path.Combine(_root, "global.json"));

        var globalPackages = await GlobalPackagesFolderAsync().ConfigureAwait(false);
        await FileSystem.File.WriteAllTextAsync(FileSystem.Path.Combine(_root, "NuGet.Config"), NuGetConfig(feed, globalPackages)).ConfigureAwait(false);

        foreach (var project in PackedProjects)
        {
            // The local packed-consumer gate stays offline: baseline validation may need nuget.org.
            var packed = await RunAsync(
                ["pack", FileSystem.Path.Combine(repository, project), "--configuration", "Release", "--no-build",
                    "--output", feed, "-p:VersionSuffix=" + VersionSuffix, "-p:EnablePackageValidation=false", "-nodeReuse:false"],
                repository).ConfigureAwait(false);
            if (packed.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "Packing " + project + " failed; the packed-consumer tests need the Release build the gate runs first.\n" + packed.Text);
            }
        }

        var package = FileSystem.Directory.EnumerateFiles(feed, "OpenCodeDotNet.Sdk.*.nupkg")
            .Select(FileSystem.Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Single(name => !name.StartsWith("OpenCodeDotNet.Sdk.Extensions.", StringComparison.Ordinal) && !name.EndsWith(".symbols", StringComparison.Ordinal));
        Version = package["OpenCodeDotNet.Sdk.".Length..];
    }

    /// <summary>Restores and builds one consumer of one package on one target framework.</summary>
    /// <param name="packageId">The package the consumer references.</param>
    /// <param name="targetFramework">The consumer's target framework.</param>
    /// <param name="suppress">Whether the consumer sets <c>SuppressTfmSupportBuildWarnings</c>.</param>
    /// <param name="cancellationToken">The test's token.</param>
    /// <returns>The build's exit code and output.</returns>
    public async Task<ConsumerBuild> BuildConsumerAsync(string packageId, string targetFramework, bool suppress, CancellationToken cancellationToken)
    {
        var directory = FileSystem.Path.Combine(
            Root, "consumer-" + Interlocked.Increment(ref _consumers).ToString(CultureInfo.InvariantCulture));
        _ = FileSystem.Directory.CreateDirectory(directory);
        var project = FileSystem.Path.Combine(directory, "Consumer.csproj");
        await FileSystem.File.WriteAllTextAsync(project, ConsumerProject(packageId, targetFramework, suppress), cancellationToken).ConfigureAwait(false);

        var built = await RunAsync(
            ["build", project, "-nologo", "-nodeReuse:false", "-tl:off", "-v:q", "-clp:NoSummary",
                "-p:RestoreConfigFile=" + FileSystem.Path.Combine(Root, "NuGet.Config")],
            directory,
            cancellationToken).ConfigureAwait(false);
        return new ConsumerBuild(built.ExitCode, built.Text);
    }

    /// <summary>
    /// Publishes one consumer of both packages as a native AOT executable and returns the publish's
    /// exit code, its full output, and the native executable's path — once per test session, like
    /// the feed itself: the ILC compile loads every core of the machine for about a minute, so every
    /// native AOT test shares one publish. The consumer targets the repository's default framework
    /// and references <c>Microsoft.Extensions.DependencyInjection</c> at the version the repository
    /// pins, both read from the repository's build files, so neither drifts from it. The output
    /// keeps warnings with their codes — the trim and AOT analyzers' <c>IL2xxx</c>/<c>IL3xxx</c>
    /// diagnostics are the subject — and <c>TreatWarningsAsErrors=false</c> keeps them reported
    /// rather than fatal.
    /// </summary>
    /// <param name="cancellationToken">The test's token.</param>
    /// <returns>The publish's exit code, output, and native executable.</returns>
    public async Task<ConsumerPublish> PublishConsumerAsync(CancellationToken cancellationToken)
    {
        if (_published is { } published)
        {
            return published;
        }

        await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _published ??= await PublishAotConsumerAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _publishGate.Release();
        }
    }

    /// <inheritdoc />
    [SlopwatchSuppress(
        "SW003",
        "Disposal is best effort: a consumer's build server may still hold a file for a moment, and a leftover temporary directory must not fail the session.")]
    public ValueTask DisposeAsync()
    {
        _publishGate.Dispose();
        if (_root is { } root && FileSystem.Directory.Exists(root))
        {
            try
            {
                FileSystem.Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: see the suppression.
            }
            catch (UnauthorizedAccessException)
            {
                // Best effort: see the suppression.
            }
        }

        return ValueTask.CompletedTask;
    }

    private static string NuGetConfig(string feed, string globalPackages) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <packageSources>
            <clear />
            <add key="packed" value="{feed}" />
            <add key="global-packages" value="{globalPackages}" />
          </packageSources>
        </configuration>
        """;

    private string ConsumerProject(string packageId, string targetFramework, bool suppress)
    {
        var suppression = suppress ? "<SuppressTfmSupportBuildWarnings>true</SuppressTfmSupportBuildWarnings>" : string.Empty;
        return $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>{targetFramework}</TargetFramework>
                <ImplicitUsings>disable</ImplicitUsings>
                <CheckEolTargetFramework>false</CheckEolTargetFramework>
                <DisableTransitiveFrameworkReferenceDownloads>true</DisableTransitiveFrameworkReferenceDownloads>
                <RestorePackagesPath>{FileSystem.Path.Combine(Root, "packages")}</RestorePackagesPath>
                {suppression}
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="{packageId}" Version="{Version}" />
              </ItemGroup>
            </Project>
            """;
    }

    private async Task<ConsumerPublish> PublishAotConsumerAsync(CancellationToken cancellationToken)
    {
        var directory = FileSystem.Path.Combine(
            Root, "consumer-" + Interlocked.Increment(ref _consumers).ToString(CultureInfo.InvariantCulture));
        _ = FileSystem.Directory.CreateDirectory(directory);
        var project = FileSystem.Path.Combine(directory, "AotConsumer.csproj");
        var targetFramework = await DefaultTargetFrameworkAsync(cancellationToken).ConfigureAwait(false);
        var dependencyInjectionVersion = await DependencyInjectionVersionAsync(cancellationToken).ConfigureAwait(false);
        await FileSystem.File.WriteAllTextAsync(project, AotConsumerProject(targetFramework, dependencyInjectionVersion), cancellationToken).ConfigureAwait(false);
        await FileSystem.File.WriteAllTextAsync(FileSystem.Path.Combine(directory, "Program.cs"), NativeAotProgram(), cancellationToken).ConfigureAwait(false);

        var publishDirectory = FileSystem.Path.Combine(directory, "publish");
        // No quiet flags here: the trim and AOT warning codes in the output are what the tests read.
        var published = await PublishAsync(
            ["publish", project, "-nologo", "--configuration", "Release",
                "--runtime", RuntimeInformation.RuntimeIdentifier,
                "--output", publishDirectory,
                "-p:PublishAot=true", "-p:TreatWarningsAsErrors=false",
                "-p:RestoreConfigFile=" + FileSystem.Path.Combine(Root, "NuGet.Config"),
                "-nodeReuse:false", "-tl:off"],
            directory,
            cancellationToken).ConfigureAwait(false);
        var executable = FileSystem.Path.Combine(
            publishDirectory, "AotConsumer" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        return new ConsumerPublish(published.ExitCode, published.Text, executable);
    }

    private string AotConsumerProject(string targetFramework, string dependencyInjectionVersion) =>
        $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>{targetFramework}</TargetFramework>
            <ImplicitUsings>disable</ImplicitUsings>
            <CheckEolTargetFramework>false</CheckEolTargetFramework>
            <DisableTransitiveFrameworkReferenceDownloads>true</DisableTransitiveFrameworkReferenceDownloads>
            <RestorePackagesPath>{FileSystem.Path.Combine(Root, "packages")}</RestorePackagesPath>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="OpenCodeDotNet.Sdk" Version="{Version}" />
            <PackageReference Include="OpenCodeDotNet.Sdk.Extensions" Version="{Version}" />
            <PackageReference Include="{DependencyInjection}" Version="{dependencyInjectionVersion}" />
          </ItemGroup>
        </Project>
        """;

    private static string NativeAotProgram() =>
        $$"""
        using System;
        using System.Collections.Generic;
        using Microsoft.Extensions.DependencyInjection;
        using OpenCode.Sdk;
        using OpenCode.Sdk.Models;

        // Construction checks use an unused endpoint; the reads below use the loopback server.
        var endpoint = new Uri("http://127.0.0.1:9/", UriKind.Absolute);

        using (new OpenCodeClient(new OpenCodeClientOptions { Endpoint = endpoint }))
        {
        }

        using (var services = new ServiceCollection()
            .AddOpenCode(options => options.Endpoint = endpoint)
            .BuildServiceProvider())
        {
            _ = services.GetRequiredService<OpenCodeClient>();
        }

        using var wireClient = new OpenCodeClient(new OpenCodeClientOptions { Endpoint = new Uri(args[0]) });
        var response = await wireClient.Sessions.GetSessionClient("ses_reader").GetMessageAsync("msg_reader");
        Equal(response.IsError, false);
        var message = (SessionMessageAssistant)response.Message;
        Equal(message.Id, "msg_reader");
        Equal(message.Content.Count, 6);
        var streaming = (SessionMessageToolStateStreaming)((SessionMessageAssistantTool)message.Content[0]).State;
        Equal(streaming.Input, "partial input");
        var running = (SessionMessageToolStateRunning)((SessionMessageAssistantTool)message.Content[1]).State;
        Equal(running.Input["command"].GetString(), "run");
        Equal(running.Metadata["phase"].GetString(), "running");
        var completed = (SessionMessageToolStateCompleted)((SessionMessageAssistantTool)message.Content[2]).State;
        Equal(completed.Input["command"].GetString(), "complete");
        Equal(completed.Metadata["exit"].GetInt32(), 0);
        Equal(completed.Content.Count, 2);
        var file = (ToolFileContent)completed.Content[0];
        Equal(file.Uri, "data:reader");
        Equal(file.Mime, "text/plain");
        Equal(file.Name, "reader.txt");
        Equal(((ToolTextContent)completed.Content[1]).Text, "completed text");
        var error = (SessionMessageToolStateError)((SessionMessageAssistantTool)message.Content[3]).State;
        Equal(error.Input["command"].GetString(), "error");
        Equal(error.Error.Type, "failure");
        Equal(error.Error.Message, "reader failure");
        Equal(error.Error.Status, 503L);
        Equal(error.Error.Response.Body, "reader body");
        Equal(error.Error.RetryAfterMs, 29L);
        Equal(error.Metadata["phase"].GetString(), "error");
        Equal(error.Content.Count, 2);
        Equal(((ToolTextContent)error.Content[0]).Text, "error text");
        Equal(((ToolFileContent)error.Content[1]).Uri, "error:reader");
        Equal(((ToolFileContent)error.Content[1]).Mime, "error/mime");
        Equal(((ToolFileContent)error.Content[1]).Name, "error.txt");
        {{MessageArmsProgram}}
        {{PromptEventsProgram}}
        Console.Out.WriteLine("{{AotConsumerData.Marker}}");
        return 0;

        static void Equal<T>(T actual, T expected)
        {
            if (!EqualityComparer<T>.Default.Equals(actual, expected))
            {
                throw new InvalidOperationException($"Expected {expected}, received {actual}.");
            }
        }
        """;

    private const string MessageArmsProgram =
        """
        var reasoningPart = (SessionMessageAssistantReasoning)message.Content[4];
        Equal(reasoningPart.Text, "reader reasoning");
        Equal(reasoningPart.Native["reason"].GetInt32(), 7);
        Equal(reasoningPart.Time.Created, 11);
        Equal(reasoningPart.Time.Completed, 13);
        var textPart = (SessionMessageAssistantText)message.Content[5];
        Equal(textPart.Text, "reader text");
        Equal(textPart.Native["text"].GetInt32(), 17);
        var list = await wireClient.Sessions.GetSessionClient("ses_reader").ListMessagesAsync();
        Equal(list.IsError, false);
        Equal(list.Messages.Count, 9);
        Equal(list.Messages[0].GetType(), typeof(SessionMessageAgentSelected));
        Equal(list.Messages[1].GetType(), typeof(SessionMessageIdle));
        Equal(list.Messages[2].GetType(), typeof(SessionMessageLocationSwitched));
        Equal(list.Messages[3].GetType(), typeof(SessionMessageModelSelected));
        Equal(list.Messages[4].GetType(), typeof(SessionMessageShell));
        Equal(list.Messages[5].GetType(), typeof(SessionMessageSkill));
        Equal(list.Messages[6].GetType(), typeof(SessionMessageSynthetic));
        Equal(list.Messages[7].GetType(), typeof(SessionMessageSystem));
        Equal(list.Messages[8].GetType(), typeof(SessionMessageUser));
        """;

    private const string PromptEventsProgram =
        """
        await using (var events = wireClient.Events.SubscribeAsync().GetAsyncEnumerator())
        {
            Equal(await events.MoveNextAsync(), true);
            Equal(events.Current.Type, "server.connected");
            Equal(await events.MoveNextAsync(), true);
            var text = (SessionTextDelta)events.Current;
            Equal(text.Id, "evt_text");
            Equal(text.Created, 17.5);
            Equal(text.Metadata["phase"].GetString(), "text");
            Equal(text.Location.Directory, "/text");
            Equal(text.Data.SessionId, "ses_text");
            Equal(text.Data.AssistantMessageId, "msg_text");
            Equal(text.Data.Ordinal, 23L);
            Equal(text.Data.Delta, "text λ \"fragment\"");
            Equal(await events.MoveNextAsync(), true);
            var reasoning = (SessionReasoningDelta)events.Current;
            Equal(reasoning.Id, "evt_reason");
            Equal(reasoning.Created, 29.5);
            Equal(reasoning.Metadata["phase"].GetString(), "reason");
            Equal(reasoning.Location.WorkspaceId, "wrk_reason");
            Equal(reasoning.Data.SessionId, "ses_reason");
            Equal(reasoning.Data.AssistantMessageId, "msg_reason");
            Equal(reasoning.Data.Ordinal, 31L);
            Equal(reasoning.Data.Delta, "reason λ fragment");
            Equal(await events.MoveNextAsync(), true);
            var input = (SessionToolInputDelta)events.Current;
            Equal(input.Id, "evt_tool");
            Equal(input.Created, 37.5);
            Equal(input.Metadata["phase"].GetString(), "tool");
            Equal(input.Location.Directory, "/tool");
            Equal(input.Data.SessionId, "ses_tool");
            Equal(input.Data.AssistantMessageId, "msg_tool");
            Equal(input.Data.Id, "tool_input");
            Equal(input.Data.Delta, "tool λ fragment");
            Equal(await events.MoveNextAsync(), true);
            var ended = (SessionTextEnded)events.Current;
            Equal(ended.Id, "evt_ended");
            Equal(ended.Created, 41.5);
            Equal(ended.Metadata["phase"].GetString(), "ended");
            Equal(ended.Location.WorkspaceId, "wrk_ended");
            Equal(ended.Durable.AggregateId, "agg_ended");
            Equal(ended.Durable.Seq, 43L);
            Equal(ended.Durable.Version, 47.5);
            Equal(ended.Data.SessionId, "ses_ended");
            Equal(ended.Data.AssistantMessageId, "msg_ended");
            Equal(ended.Data.Ordinal, 53L);
            Equal(ended.Data.Text, "ended λ text");
            Equal(ended.Data.Native["state"][0].GetInt32(), 59);
            Equal(await events.MoveNextAsync(), true);
            var started = (SessionStepStarted)events.Current;
            Equal(started.Id, "evt_started");
            Equal(started.Location.Directory, "/started");
            Equal(started.Durable.Seq, 353L);
            Equal(started.Data.SessionId, "ses_started");
            Equal(started.Data.Agent, "build");
            Equal(started.Data.Model.Id, "model_started");
            Equal(started.Data.Started, 367);
            Equal(await events.MoveNextAsync(), true);
            var asked = (PermissionAsked)events.Current;
            Equal(asked.Id, "evt_permission");
            Equal(asked.Data.Id, "per_asked");
            Equal(asked.Data.Resources.Count, 2);
            Equal(asked.Data.Resources[0], "a");
            Equal(asked.Data.Resources[1], "b");
            Equal(asked.Data.Message, "permission message");
            Equal(await events.MoveNextAsync(), true);
            var created = (SessionCreated)events.Current;
            Equal(created.Id, "evt_created");
            Equal(created.Location.WorkspaceId, "wrk_created");
            Equal(created.Durable.Version, 193.5);
            Equal(created.Data.SessionId, "ses_created");
            Equal(created.Data.Location.Directory, "/data-created");
            Equal(created.Data.Permissions.Count, 1);
            Equal(created.Data.Permissions[0].Resource, "*.cs");
            Equal(created.Data.Model.ProviderId, "provider_created");
        }
        """;

    private async Task<string> DefaultTargetFrameworkAsync(CancellationToken cancellationToken)
    {
        var document = await RepositoryDocumentAsync("Directory.Build.props", cancellationToken).ConfigureAwait(false);
        return (string?)document.Root?.Elements("PropertyGroup").Elements("DefaultTargetFramework").SingleOrDefault()
            ?? throw new InvalidOperationException("The repository's Directory.Build.props names no DefaultTargetFramework.");
    }

    private async Task<string> DependencyInjectionVersionAsync(CancellationToken cancellationToken)
    {
        var document = await RepositoryDocumentAsync("Directory.Packages.props", cancellationToken).ConfigureAwait(false);
        return document.Root?.Elements("ItemGroup").Elements("PackageVersion")
                .Where(element => (string?)element.Attribute("Include") == DependencyInjection)
                .Select(element => (string?)element.Attribute("Version"))
                .SingleOrDefault()
            ?? throw new InvalidOperationException("The repository's Directory.Packages.props pins no " + DependencyInjection + " version.");
    }

    private async Task<XDocument> RepositoryDocumentAsync(string fileName, CancellationToken cancellationToken)
    {
        var text = await FileSystem.File.ReadAllTextAsync(FileSystem.Path.Combine(Repository, fileName), cancellationToken).ConfigureAwait(false);
        return XDocument.Parse(text);
    }

    private async Task<string> GlobalPackagesFolderAsync()
    {
        var listed = await RunAsync(["nuget", "locals", "global-packages", "--list"], Root).ConfigureAwait(false);
        const string prefix = "global-packages:";
        var line = listed.Text.Split('\n').Select(static text => text.Trim()).FirstOrDefault(text => text.StartsWith(prefix, StringComparison.Ordinal));
        return line is null
            ? throw new InvalidOperationException("The global packages folder could not be read:\n" + listed.Text)
            : line[prefix.Length..].Trim();
    }

    /// <summary>
    /// Runs the native AOT publish. On Windows, ILC locates the linker through Visual Studio's
    /// vcvarsall, and the Visual Studio 2026 one calls vswhere by name, so a PATH without the
    /// installer's folder leaves vswhere's error text inside the linker command line. The publish
    /// therefore gets vswhere's documented fixed folder on its PATH, whatever the caller's PATH is;
    /// nothing else about its environment changes.
    /// </summary>
    private static async Task<(int ExitCode, string Text)> PublishAsync(IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var installer = FileSystem.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer");
        if (OperatingSystem.IsWindows() && FileSystem.Directory.Exists(installer))
        {
            path = installer + FileSystem.Path.PathSeparator + path;
        }

        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        var result = await Cli.Wrap("dotnet")
            .WithArguments(arguments)
            .WithWorkingDirectory(workingDirectory)
            .WithEnvironmentVariables(environment => environment.Set("PATH", path))
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(standardOutput))
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(standardError))
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);
        return (result.ExitCode, standardOutput.ToString() + standardError);
    }

    private async Task<(int ExitCode, string Text)> RunAsync(IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync("dotnet", arguments, workingDirectory, cancellationToken).ConfigureAwait(false);
        return (result.ExitCode, Encoding.UTF8.GetString(result.StandardOutput) + result.StandardError);
    }
}
