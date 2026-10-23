using System.Text.Json;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Serialization;
using OpenCode.Sdk.Tools.Tests.Generator.Binding.Support;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

/// <summary>
/// Holds the bound query types to upstream's own generated client at the pin (ADR-0037). The
/// document projects some numeric query parameters as strings; upstream's client takes the number
/// its server decodes (<see cref="UpstreamClientContract"/> reads that client). A parameter the SDK
/// binds as a string where that client takes a number, or as an integer where it takes anything
/// else, fails here, and so does a type the oracle cannot classify, so a refresh that changes either
/// side is reviewed.
/// </summary>
public sealed class UpstreamClientQueryTypeTests
{
    private const string ClientRoot = "external/opencode/packages/client/src";

    /// <summary>
    /// Upstream type names that are not components of the document, with what they carry. A name
    /// missing from both fails the oracle rather than being guessed.
    /// </summary>
    private static readonly Dictionary<string, bool> NamedTypeIsNumeric = new(StringComparer.Ordinal)
    {
        // The message id brand; the document names the same string component Session.Message.ID.
        ["SessionMessage.ID"] = false,
        // The session list cursor brand; the document names the same string component SessionsCursor.
        ["Session.ListCursor"] = false,
    };

    [Test]
    public async Task Every_Bound_Query_Parameter_Should_Take_The_Type_Upstreams_Client_Takes()
    {
        var contract = await ReadContractAsync();
        var (document, _, _) = await BindingTestHost.LoadPinnedInputsAsync();
        var bound = await BoundSubjectsAsync();

        var problems = new List<string>();
        foreach (var subject in bound)
        {
            if (!contract.TryGetQueryType(subject.Operation.HttpMethod, subject.Operation.RouteTemplate, subject.Property.WireName, out var type, out var problem))
            {
                problems.Add($"{subject.Key}: {problem}");
            }
            else if (UpstreamClientContract.IsNumeric(type, name => ResolveNamed(name, document)) is not { } numeric)
            {
                problems.Add($"{subject.Key}: upstream's type '{type}' is neither a known number nor a known non-number");
            }
            else if (numeric != subject.Property.Kind is QueryValueKind.Int32 or QueryValueKind.Int64)
            {
                problems.Add($"{subject.Key}: upstream's client takes '{type}', the SDK binds {subject.Property.Kind}");
            }
        }

        await Assert.That(problems).IsEmpty().Because(string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// The oracle checks what the binder bound, so this holds the bound subjects to the document's
    /// own inventory: every selected operation's query parameter that is a plain string or a number
    /// is bound as text or an integer, and nothing else is. A parameter the binder dropped would
    /// otherwise leave the oracle without anything to check.
    /// </summary>
    [Test]
    public async Task The_Bound_Subjects_Should_Be_Every_Plain_String_Or_Number_Query_Parameter_Of_The_Selection()
    {
        var (document, selection, _) = await BindingTestHost.LoadPinnedInputsAsync();
        var selected = selection.OperationIds.ToHashSet(StringComparer.Ordinal);
        var expected = document.Operations
            .Where(operation => selected.Contains(operation.OperationId))
            .SelectMany(operation => operation.Parameters
                .Where(parameter => parameter is { Location: SpecParameterLocation.Query, IsDeepObject: false } && IsScalarText(document, parameter.Schema))
                .Select(parameter => operation.OperationId + ":" + parameter.Name))
            .Order(StringComparer.Ordinal);

        var bound = (await BoundSubjectsAsync()).Select(static subject => subject.Key).Order(StringComparer.Ordinal);

        await Assert.That(bound).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    /// <summary>
    /// A type row's evidence is a file the source watch pins, and the pinned file still declares
    /// the decode the row quotes.
    /// </summary>
    [Test]
    public async Task Every_Type_Row_Should_Quote_A_Decode_In_A_Watched_File()
    {
        var fileSystem = new RealFileSystem();
        var root = new RepositoryRoot(fileSystem).Locate();
        var (_, _, curation) = await BindingTestHost.LoadPinnedInputsAsync();
        var watchText = await fileSystem.File.ReadAllTextAsync(fileSystem.Path.Combine(root, "spec", "source-watch.json"), CancellationToken.None);
        var watched = JsonSerializer.Deserialize(watchText, ToolJsonContext.Default.SourceWatch)!.Sources
            .Select(static source => source.Path)
            .ToHashSet(StringComparer.Ordinal);

        var problems = new List<string>();
        foreach (var row in curation.QueryParameterTypes)
        {
            var subject = $"{row.OperationId}:{row.Parameter}";
            if (!watched.Contains(row.Evidence.Path))
            {
                problems.Add($"{subject}: '{row.Evidence.Path}' is not in the source watch");
                continue;
            }

            var source = await fileSystem.File.ReadAllTextAsync(
                fileSystem.Path.Combine(root, "external", "opencode", row.Evidence.Path), CancellationToken.None);
            if (!source.Contains(row.Evidence.Anchor, StringComparison.Ordinal))
            {
                problems.Add($"{subject}: '{row.Evidence.Path}' no longer declares '{row.Evidence.Anchor}'");
            }
        }

        await Assert.That(curation.QueryParameterTypes).IsNotEmpty();
        await Assert.That(problems).IsEmpty().Because(string.Join(Environment.NewLine, problems));
    }

    /// <summary>The query properties the pinned bind produced as text or an integer, each with its operation.</summary>
    private static async Task<IReadOnlyList<BoundSubject>> BoundSubjectsAsync()
    {
        var plan = await new BindingTestHost().BindPinnedAsync();
        return
        [
            .. plan.Clients
                .SelectMany(static client => client.Operations)
                .SelectMany(static operation => (operation.QueryRequest?.Properties ?? [])
                    .Where(static property => property.Kind is QueryValueKind.Text or QueryValueKind.Int32 or QueryValueKind.Int64)
                    .Select(property => new BoundSubject(operation, property))),
        ];
    }

    /// <summary>A plain string or a number once references and a nullable wrapper are followed, as the binder classifies it.</summary>
    private static bool IsScalarText(SpecDocument document, SchemaNode schema)
    {
        var resolved = RefChain.Resolve(schema, document.Schemas).Node;
        var value = resolved is NullableNode { Format: null } nullable ? RefChain.Resolve(nullable.Inner, document.Schemas).Node : resolved;
        return value is PrimitiveNode { Kind: PrimitiveKind.String, Format: null } or PrimitiveNode { Kind: PrimitiveKind.Integer or PrimitiveKind.Number };
    }

    /// <summary>A named upstream type is what the document's component of that name is, or what <see cref="NamedTypeIsNumeric"/> records.</summary>
    private static bool? ResolveNamed(string name, SpecDocument document)
    {
        if (document.Schemas.TryGetValue(name, out var schema))
        {
            return schema switch
            {
                PrimitiveNode { Kind: PrimitiveKind.Integer or PrimitiveKind.Number } => true,
                PrimitiveNode { Kind: PrimitiveKind.String } or EnumNode => false,
                _ => null,
            };
        }

        return NamedTypeIsNumeric.TryGetValue(name, out var numeric) ? numeric : null;
    }

    private static async Task<UpstreamClientContract> ReadContractAsync()
    {
        var fileSystem = new RealFileSystem();
        var root = new RepositoryRoot(fileSystem).Locate();
        return UpstreamClientContract.Parse(
            await ReadAsync(fileSystem, root, "promise/generated/client.ts"),
            await ReadAsync(fileSystem, root, "effect/api/api.ts"),
            await ReadAsync(fileSystem, root, "promise/generated/types.ts"));
    }

    private static async Task<string> ReadAsync(RealFileSystem fileSystem, string root, string path)
    {
        var full = fileSystem.Path.Combine(root, ClientRoot, path);
        if (!fileSystem.File.Exists(full))
        {
            throw new InvalidOperationException(
                $"Upstream's client is missing at '{full}'. Run `git submodule update --init --depth 1 external/opencode` first.");
        }

        return await fileSystem.File.ReadAllTextAsync(full, CancellationToken.None);
    }

    private sealed record BoundSubject(OperationPlan Operation, QueryPropertyPlan Property)
    {
        public string Key => Operation.OperationId + ":" + Property.WireName;
    }
}
