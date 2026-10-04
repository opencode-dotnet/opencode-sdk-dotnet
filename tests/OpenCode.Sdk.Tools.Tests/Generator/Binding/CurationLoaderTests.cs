using System.Reflection;
using System.Text.Json.Serialization;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions.Testing;
using Testably.Abstractions.Testing.Initializer;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

public sealed class CurationLoaderTests
{
    private const string CurationPath = "tools/curation.json";

    [Test]
    public async Task LoadAsync_Should_Read_Strict_Curation()
    {
        var fileSystem = CreateFileSystem("Binding.valid-curation.json");

        var curation = await new CurationLoader(fileSystem).LoadAsync(CurationPath, CancellationToken.None);

        await Assert.That(curation.Groups["health"].Placement).IsEqualTo(GroupPlacement.Root);
        await Assert.That(curation.Groups["health"].Emission).IsEqualTo(EmissionMode.Public);
        await Assert.That(curation.OperationIdentities).IsEmpty();
        await Assert.That(curation.OperationNames).IsEmpty();
        await Assert.That(curation.SchemaNames).IsEmpty();
        await Assert.That(curation.EnvelopePayloadNames).IsEmpty();
        await Assert.That(curation.SchemaAliases).IsEmpty();
        await Assert.That(curation.TransportOwned).IsEmpty();
        await Assert.That(curation.Declined).IsEmpty();

        var hoisted = curation.HoistedMemberNames.Single();
        await Assert.That(hoisted.Owner).IsEqualTo("ISessionEventDurable");
        await Assert.That(hoisted.Property).IsEqualTo("durable");
        await Assert.That(hoisted.DotNetName).IsEqualTo("IDurableEnvelope");
        await Assert.That(hoisted.Reason).Contains("durable envelope");
    }

    [Test]
    public async Task LoadAsync_Should_Read_Reasoned_Declined_Rows()
    {
        var fileSystem = CreateFileSystem("Binding.declined-curation.json");

        var curation = await new CurationLoader(fileSystem).LoadAsync(CurationPath, CancellationToken.None);

        var row = curation.Declined.Single();
        await Assert.That(row.OperationId).IsEqualTo("fs.read");
        await Assert.That(row.Reason).Contains("wildcard");
    }

    [Test]
    public async Task LoadAsync_Should_Read_Reasoned_Redacted_Open_Members_Rows()
    {
        var fileSystem = CreateFileSystem("Binding.redacted-open-members-curation.json");

        var curation = await new CurationLoader(fileSystem).LoadAsync(CurationPath, CancellationToken.None);

        var row = curation.RedactedOpenMembers.Single();
        await Assert.That(row.Model).IsEqualTo("ProviderSettings");
        await Assert.That(row.Redact).IsTrue();
        await Assert.That(row.Reason).Contains("provider options");
    }

    [Test]
    public async Task GenerationCuration_Should_Expose_Only_Allowed_Curation_Sections()
    {
        var sections = typeof(GenerationCuration)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(static property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                                       ?? throw new InvalidOperationException($"Curation property '{property.Name}' has no JSON name."))
            .Order(StringComparer.Ordinal);

        await Assert
            .That(sections)
            .IsEquivalentTo([
                "declined", "enumMemberNames", "envelopePayloadNames", "groups", "hoistedMemberNames", "operationIdentities",
                "operationNames", "redactedMembers", "redactedOpenMembers", "schemaAliases", "schemaNames", "secretLookingNames", "transportOwned"
            ]);
    }

    [Test]
    public async Task LoadAsync_Should_Read_Reasoned_Operation_Name_Rows()
    {
        var fileSystem = CreateFileSystem("Binding.operation-name-curation.json");

        var curation = await new CurationLoader(fileSystem).LoadAsync(CurationPath, CancellationToken.None);

        var operationName = curation.OperationNames.Single();
        await Assert.That(operationName.OperationId).IsEqualTo("event.subscribe");
        await Assert.That(operationName.MethodName).IsEqualTo("SubscribeAsync");
        await Assert.That(operationName.Reason).Contains("reviewed public surface");
        var schemaName = curation.SchemaNames.Single();
        await Assert.That(schemaName.Schema).IsEqualTo("V2Event");
        await Assert.That(schemaName.DotNetName).IsEqualTo("IEvent");
        await Assert.That(schemaName.Reason).Contains("transport prefix");
    }

    [Test]
    public async Task LoadAsync_Should_Read_Schema_Alias_Rows()
    {
        var fileSystem = CreateFileSystem("Binding.alias-curation.json");

        var curation = await new CurationLoader(fileSystem).LoadAsync(CurationPath, CancellationToken.None);

        var alias = curation.SchemaAliases.Single();
        await Assert.That(alias.Schema).IsEqualTo("InvalidRequestError1");
        await Assert.That(alias.AliasOf).IsEqualTo("InvalidRequestError");
        await Assert.That(alias.Reason).Contains("duplicate");
    }

    [Test]
    public async Task LoadAsync_Should_Read_The_Internal_Raw_Emission_Row()
    {
        var fileSystem = CreateFileSystem("Binding.internal-raw-curation.json");

        var curation = await new CurationLoader(fileSystem).LoadAsync(CurationPath, CancellationToken.None);

        await Assert.That(curation.Groups["pty"].Emission).IsEqualTo(EmissionMode.InternalRaw);
        await Assert.That(curation.Groups["pty"].ClientName).IsEqualTo("Ptys");
    }

    [Test]
    public async Task LoadAsync_Should_Read_Transport_Owned_Rows()
    {
        var fileSystem = CreateFileSystem("Binding.transport-owned-curation.json");

        var curation = await new CurationLoader(fileSystem).LoadAsync(CurationPath, CancellationToken.None);

        var row = curation.TransportOwned.Single();
        await Assert.That(row.OperationId).IsEqualTo("pty.connect");
        await Assert.That(row.SubtreeSha256.Length).IsEqualTo(64);
        await Assert.That(row.Reason).Contains("hand-written");
    }

    [Test]
    public async Task LoadAsync_Should_Refuse_An_Unknown_Emission_Value()
    {
        var fileSystem = CreateFileSystem("Binding.unknown-emission-curation.json");

        var exception = await Assert
            .That(async () => _ = await new CurationLoader(fileSystem).LoadAsync(CurationPath, CancellationToken.None))
            .Throws<BindingException>();

        await Assert.That(exception!.Errors.Single().Category).IsEqualTo(BindingErrorCategory.Curation);
        await Assert.That(exception.Errors.Single().Problem).Contains("emission");
    }

    /// <summary>An open bag's row must say which way it decides; a row without the verdict decides nothing.</summary>
    [Test]
    public async Task LoadAsync_Should_Refuse_A_Redacted_Open_Members_Row_Without_A_Verdict()
    {
        var fileSystem = CreateFileSystem("Binding.redacted-open-members-missing-redact-curation.json");

        var exception = await Assert
            .That(async () => _ = await new CurationLoader(fileSystem).LoadAsync(CurationPath, CancellationToken.None))
            .Throws<BindingException>();

        await Assert.That(exception!.Errors.Single().Category).IsEqualTo(BindingErrorCategory.Curation);
        await Assert.That(exception.Errors.Single().Problem).Contains("redact");
    }

    [Test]
    public async Task LoadAsync_Should_Refuse_Unknown_Fields()
    {
        var fileSystem = CreateFileSystem("Binding.unknown-curation-field.json");

        var exception = await Assert
            .That(async () => _ = await new CurationLoader(fileSystem).LoadAsync(CurationPath, CancellationToken.None))
            .Throws<BindingException>();

        await Assert.That(exception!.Errors.Single().Category).IsEqualTo(BindingErrorCategory.Curation);
        await Assert.That(exception.Errors.Single().Problem).Contains("mystery");
    }

    [Test]
    [Arguments("Binding.property-overrides-curation.json", "propertyOverrides")]
    [Arguments("Binding.mutually-exclusive-queries-curation.json", "mutuallyExclusiveQueries")]
    public async Task LoadAsync_Should_Refuse_Forbidden_Semantic_Curation(string fixtureName, string section)
    {
        var fileSystem = CreateFileSystem(fixtureName);

        var exception = await Assert
            .That(async () => _ = await new CurationLoader(fileSystem).LoadAsync(CurationPath, CancellationToken.None))
            .Throws<BindingException>();

        await Assert.That(exception!.Errors.Single().Category).IsEqualTo(BindingErrorCategory.Curation);
        await Assert.That(exception.Errors.Single().Problem).Contains(section);
    }

    private static MockFileSystem CreateFileSystem(string fixtureName)
    {
        var fileSystem = new MockFileSystem();
        fileSystem.Initialize().With(new FileDescription(CurationPath, new FixtureLoader().Load(fixtureName)));
        return fileSystem;
    }
}
