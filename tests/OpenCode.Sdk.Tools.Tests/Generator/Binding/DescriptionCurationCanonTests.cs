using System.Text.Json;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Serialization;
using OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

public sealed class DescriptionCurationCanonTests
{
    [Test]
    [Arguments("GetPingAsync")]
    [Arguments("GoneAsync")]
    public async Task Apply_Should_Address_Operation_Documentation(string member)
    {
        var plan = EmitterPlanFixture.Create();
        var row = new DescriptionCuration
        {
            Type = "OpenCodeClient",
            Member = member,
            Summary = "The curated <ping>.",
            Remarks = "Keep & reconnect.",
            Reason = "Prove operation documentation.",
            Sources = [new() { Location = "packages/example.ts:1", Excerpt = "An explicit declaration." }],
        };
        var errors = new BindingErrorCollector();
        var bound = DescriptionCurationBinder.Apply(plan, [row], errors);
        await Assert.That(errors.Snapshot()).IsEquivalentTo(member == "GoneAsync" ? new[]
        {
            new BindingError(BindingErrorCategory.Curation, "OpenCodeClient.GoneAsync", "description member does not exist as a declared property on the generated type"),
        } : [], CollectionOrdering.Matching);
        if (member == "GoneAsync") { return; }
        var xml = OperationMethodEmitter.Emit(bound.Clients[0].Operations[0], EmissionMode.Public).GetLeadingTrivia().ToFullString();
        await Assert.That(xml.Trim()).IsEqualTo("""
            /// <summary>
            /// The curated &lt;ping&gt;.
            /// </summary>
            /// <param name="requestOptions">The per-call options.</param>
            /// <param name="cancellationToken">The cancellation token.</param>
            /// <returns>The &apos;PingResponse&apos; envelope.</returns>
            /// <exception cref="OpenCodeApiException">The API returned an error status (declared: 400) and NoThrow was not selected.</exception>
            /// <exception cref="OpenCodeTransportException">The server could not be reached or returned a malformed success body.</exception>
            /// <remarks>
            /// Operation <c>ping.get</c>: <c>GET /api/ping</c>. Keep &amp; reconnect.
            /// </remarks>
            """);
    }

    [Test]
    [Arguments("packages/example.ts:1", "docs/architecture/example.md#contract", "must declare exactly one of location or canon")]
    [Arguments(null, null, "must declare exactly one of location or canon")]
    [Arguments(null, "docs/guide/example.md#contract", "canon must be a docs/architecture/-relative .md path with a heading slug and no '..'")]
    [Arguments(null, "docs/architecture/example.md", "canon must be a docs/architecture/-relative .md path with a heading slug and no '..'")]
    [Arguments(null, "docs/architecture/../example.md#contract", "canon must be a docs/architecture/-relative .md path with a heading slug and no '..'")]
    [Arguments(null, "docs/architecture/example.md#contract", null)]
    public async Task Apply_Should_Validate_Canon_Source_Shapes(string? location, string? canon, string? problem)
    {
        var curation = Curation(Groups("example", RootGroup())) with
        {
            Descriptions = [new()
            {
                Type = "ExampleItem", Summary = "The documented item.", Reason = "Prove the source contract.",
                Sources = [new() { Location = location, Canon = canon, Excerpt = "An explicit declaration." }],
            }],
        };
        var loaded = JsonSerializer.Deserialize(JsonSerializer.Serialize(curation, ToolJsonContext.Default.GenerationCuration), ToolJsonContext.Default.GenerationCuration)!;
        var errors = new BindingErrorCollector();
        _ = DescriptionCurationBinder.Apply(EmitterPlanFixture.Create(), loaded.Descriptions, errors);

        await Assert.That(errors.Snapshot()).IsEquivalentTo(problem is null ? [] : new[]
        {
            new BindingError(BindingErrorCategory.Curation, "ExampleItem", "description sources[0] " + problem),
        }, CollectionOrdering.Matching);
    }
}
