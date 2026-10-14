using System.Text;
using System.Text.Json;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Generator.Ingestion;
using OpenCode.Sdk.Tools.Serialization;
using Testably.Abstractions.Testing;
using Testably.Abstractions.Testing.Initializer;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Support;

internal static class GenerationTestData
{
    public const string ManifestPath = "src/OpenCode.Sdk/.generated-manifest.json";
    public const string MarkerPath = "src/OpenCode.Sdk/.generation-incomplete";
    public const string OutputRoot = "src/OpenCode.Sdk";
    public const string ProjectPath = "src/OpenCode.Sdk/OpenCode.Sdk.csproj";

    /// <summary>The WebSocket operation the transport-owned command roots pin for a hand-written door.</summary>
    public const string TransportOwnedOperationId = "pty.connect";

    /// <summary>The walled operation the declined command roots leave out of the generated surface by decision.</summary>
    public const string DeclinedOperationId = "widget.tail";

    private const string DeclinedReason =
        "The operation is WebSocket-marked and a bodyless PUT, so it does not bind; maintainer 2026-08-30.";

    private const string SpecPath = "spec/openapi.json";
    private const string ProfilePath = "tools/generation-profile.txt";
    private const string CurationPath = "tools/curation.json";

    /// <summary>A reach report as the coordinator composes it, and a stale one a refresh would replace.</summary>
    public const string ReachReport = "# reach\nExampleHealth owner=Health reached-by=Health\n";

    public const string StaleReachReport = "# reach\nExampleHealth owner=Health reached-by=Events,Health\n";

    /// <summary>Provenance-headed content for a generated file that already sits on disk.</summary>
    public static string OwnedContent(string content) => $"{GenerationProvenance.Header}{content}";

    public static MockFileSystem CreateFileSystem()
    {
        var fileSystem = new MockFileSystem();
        fileSystem.Initialize().With(new FileDescription(ProjectPath, "<Project />"));
        return fileSystem;
    }

    /// <summary>The generate command's repository roots: one selected operation, two pending, no transport-owned row.</summary>
    public static MockFileSystem CreateCommandFileSystem()
    {
        var fileSystem = CreateCommandRoots(extend: null);
        WriteCuration(fileSystem, transportOwned: []);
        return fileSystem;
    }

    /// <summary>The command roots with one walled pending operation declined, so the marker carries both maps at once.</summary>
    public static MockFileSystem CreateDeclinedCommandFileSystem()
    {
        var fileSystem = CreateCommandRoots(extend: null);
        WriteCuration(fileSystem, transportOwned: [], declined: [Declined(DeclinedOperationId, DeclinedReason)]);
        return fileSystem;
    }

    /// <summary>
    /// Roots where every unselected operation is walled and declined, so pending reaches zero while
    /// the marker stays — the arrangement the packing wall opens on.
    /// </summary>
    public static MockFileSystem CreateFullyDeclinedCommandFileSystem()
    {
        var fileSystem = CreateCommandRoots(extend: null, withBindablePending: false);
        WriteCuration(
            fileSystem,
            transportOwned: [],
            declined:
            [
                Declined("session.list", "The success response declares no JSON schema, so the operation does not bind; maintainer 2026-08-30."),
                Declined(DeclinedOperationId, DeclinedReason),
            ]);
        return fileSystem;
    }

    /// <summary>The command roots with a decline over the operation that binds cleanly — the wall the decline must not cover.</summary>
    public static MockFileSystem CreateBindableDeclineCommandFileSystem()
    {
        var fileSystem = CreateCommandRoots(extend: null);
        WriteCuration(
            fileSystem,
            transportOwned: [],
            declined: [Declined("plugin.list", "A reason that claims a wall the probe cannot find.")]);
        return fileSystem;
    }

    /// <summary>
    /// The command roots plus a WebSocket operation covered by a <c>transportOwned</c> row whose
    /// fingerprint is computed from the scenario's own ingested shape, the way the committed row is.
    /// </summary>
    public static async Task<MockFileSystem> CreateTransportOwnedCommandFileSystemAsync(CancellationToken cancellationToken = default)
    {
        var fileSystem = CreateCommandRoots(spec => spec
            .WithOperation(TransportOwnedOperationId, path: "/api/pty/{ptyID}/connect", configure: operation => operation
                .Parameter("ptyID", "path", schema => schema.Type("string"), required: true)
                .Extension("x-websocket", "true")));
        var document = await new SpecIngestion(fileSystem).IngestAsync(SpecPath, cancellationToken);
        var operation = document.Operations.Single(static candidate => candidate.OperationId == TransportOwnedOperationId);
        var row = TransportOwned(TransportOwnedOperationId, TransportOwnedFingerprint.ComputeSha256(operation));
        WriteCuration(fileSystem, transportOwned: [row]);
        return fileSystem;
    }

    /// <summary>The command roots without the owner row, so the selected operation's unprefixed model stands unplaced.</summary>
    public static MockFileSystem CreateUnplacedModelCommandFileSystem()
    {
        var fileSystem = CreateCommandRoots(extend: null);
        WriteCuration(fileSystem, Curation(Groups("health", RootGroup())));
        return fileSystem;
    }

    public static GeneratedSource Source(string relativePath, string source) =>
        new()
        {
            RelativePath = relativePath,
            Utf8Source = Encoding.UTF8.GetBytes(OwnedContent(source)),
        };

    private static MockFileSystem CreateCommandRoots(Action<SpecDocumentBuilder>? extend, bool withBindablePending = true)
    {
        var spec = new SpecDocumentBuilder()
            .WithSchema("ExampleHealth", schema => schema
                .Type("object")
                .Property("healthy", property => property.Type("boolean"), required: true))
            .WithOperation("health.get", path: "/api/health", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("ExampleHealth")))
            .WithOperation("session.list", path: "/api/session")
            // Two independent wire-shape walls (WebSocket, a bodyless PUT) on the same operation,
            // so the pending map proves the telltale lists every wall, not only the first.
            .WithOperation("widget.tail", method: "put", path: "/api/widget/tail", configure: operation => operation
                .Extension("x-websocket", "true"));
        // The bindable pending operation is the drift telltale's subject; roots that must reach
        // pending = 0 leave it out, because a bindable operation can never be declined.
        if (withBindablePending)
        {
            _ = spec
                .WithSchema("ExamplePlugin", schema => schema
                    .Type("object")
                    .Property("name", property => property.Type("string"), required: true))
                .WithOperation("plugin.list", path: "/api/plugin", configure: operation => operation
                    .Response(200, "application/json", schema => schema.Ref("ExamplePlugin")));
        }

        extend?.Invoke(spec);
        var fileSystem = CreateFileSystem();
        fileSystem
            .Initialize()
            .With(
                new FileDescription(SpecPath, spec.BuildJson()),
                new FileDescription(ProfilePath, "health.get\n"));
        return fileSystem;
    }

    /// <summary>Writes the command curation as the loader reads it: the root-placed health group, the owner row its model needs, and the given non-selected admission rows.</summary>
    private static void WriteCuration(MockFileSystem fileSystem, IReadOnlyList<TransportOwnedCuration> transportOwned,
        IReadOnlyList<DeclinedCuration>? declined = null)
    {
        var curation = Curation(
            Groups("health", RootGroup()),
            transportOwned: transportOwned,
            declined: declined,
            modelOwners: [Owner("ExampleHealth", "Health")]);
        fileSystem
            .Initialize()
            .With(new FileDescription(CurationPath, JsonSerializer.Serialize(curation, ToolJsonContext.Default.GenerationCuration)));
    }

    private static void WriteCuration(MockFileSystem fileSystem, GenerationCuration curation) =>
        fileSystem
            .Initialize()
            .With(new FileDescription(CurationPath, JsonSerializer.Serialize(curation, ToolJsonContext.Default.GenerationCuration)));
}
