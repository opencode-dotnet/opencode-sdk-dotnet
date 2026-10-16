using System.Reflection;
using System.Text.Json.Serialization;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

public sealed class CurationRowContractTests
{
    public static IEnumerable<object[]> RegisteredRows() => CurationRowContract.RegisteredProperties.Select<string, object[]>(static property =>
        property switch
        {
            "Descriptions" => [property, "description", "Type", "subject"],
            "SessionScopedEvent" => [property, "session-scoped event", "", "sessionScopedEvent"],
            "Groups" => [property, "group", "group", "subject"],
            "OperationIdentities" => [property, "operation identity", "OperationId", "subject"],
            "OperationNames" => [property, "operation name", "OperationId", "subject"],
            "ParameterNames" => [property, "parameter name", "OperationId,Parameter", "subject:parameter"],
            "SchemaNames" => [property, "schema name", "Schema", "subject"],
            "EnvelopePayloadNames" => [property, "envelope payload name", "OperationId", "subject"],
            "SchemaAliases" => [property, "schema alias", "Schema", "subject"],
            "TransportOwned" => [property, "transport-owned", "OperationId", "subject"],
            "HoistedMemberNames" => [property, "hoisted member name", "Owner,Property", "subject"],
            "EnumMemberNames" => [property, "enum member name", "Schema,Value", "subject/value"],
            "RedactedMembers" => [property, "redacted member", "Model,Property", "subject.property"],
            "RedactedOpenMembers" => [property, "redacted open members", "Model", "subject"],
            "SecretLookingNames" => [property, "secret-looking name", "Property", "subject"],
            "Declined" => [property, "declined", "OperationId", "subject"],
            "ModelOwners" => [property, "model owner", "Match", "subject"],
            "SessionAreas" => [property, "session area", "Area", "subject"],
            "QueryParameterTypes" => [property, "query parameter type", "OperationId,Parameter", "subject:parameter"],
            "StringQueryParameters" => [property, "string query parameter", "OperationId,Parameter", "subject:parameter"],
            _ => throw new InvalidOperationException("Missing contract arrangement: " + property),
        });

    [Test]
    [MethodDataSource(nameof(RegisteredRows))]
    public async Task Validate_Should_Enforce_Every_Registered_Row_Contract(string propertyName, string label, string keys, string subject)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var property = typeof(GenerationCuration).GetProperty(propertyName)!;
        var section = property.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name;
        var row = ContractRow(propertyName);
        var reason = row.GetType().GetProperty("Reason")!;
        foreach (var blank in new[] { null, "", " " })
        {
            reason.SetValue(row, blank);
            var errors = ValidateRows(property, row);
            await Assert.That(errors.Single()).IsEqualTo(new BindingError(BindingErrorCategory.Curation, subject, label + " curation must declare a reason"));
        }

        var refusal = await BindContractRowAsync(propertyName, row);
        await Assert.That(refusal.Errors.Single(error => error.Problem == label + " curation must declare a reason")).IsEqualTo(new BindingError(BindingErrorCategory.Curation, subject, label + " curation must declare a reason"));

        reason.SetValue(row, "Scenario row has a reviewed reason.");
        await Assert.That(ValidateRows(property, row)).IsEmpty();
        if (propertyName is not ("Groups" or "SessionScopedEvent"))
        {
            await Assert.That(ValidateRows(property, row, count: 2).Single()).IsEqualTo(new BindingError(BindingErrorCategory.Curation, subject, label + " curation is duplicated"));
        }
        foreach (var key in keys.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var field = row.GetType().GetProperty(key);
            var value = field?.GetValue(row);
            foreach (var blank in new[] { null, "", " " })
            {
                field?.SetValue(row, blank);
                var errors = ValidateRows(property, row, groupKey: blank);
                var name = field?.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? "group";
                await Assert.That(errors.Single()).IsEqualTo(new BindingError(BindingErrorCategory.Curation,
                    section + "[0]", section + "[0] curation names no " + name));
            }
            field?.SetValue(row, value);
        }
    }

    private static Array Rows(IReasonedRow row, int count)
    {
        var rows = Array.CreateInstance(row.GetType(), count);
        for (var index = 0; index < count; index++)
        {
            rows.SetValue(row, index);
        }
        return rows;
    }

    private static IReadOnlyList<BindingError> ValidateRows(PropertyInfo property, IReasonedRow row, int count = 1,
        string? groupKey = "subject", IReasonedRow? secondRow = null)
    {
        var rows = property.Name == "Groups" ? new[] { new KeyValuePair<string, GroupCuration>(groupKey!, (GroupCuration)row) } : Rows(row, count);
        if (secondRow is not null)
        {
            rows.SetValue(secondRow, 1);
        }
        return (IReadOnlyList<BindingError>)typeof(CurationRowContractTests).GetMethod(nameof(ValidateTyped))!
            .MakeGenericMethod(rows.GetType().GetElementType()!).Invoke(null, [property.Name, rows])!;
    }

    public static IReadOnlyList<BindingError> ValidateTyped<TRow>(string property, Array rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var errors = new BindingErrorCollector();
        _ = CurationRowContract.Validate(property, rows.Cast<TRow>(), errors);
        return errors.Count == 0 ? [] : Assert.Throws<BindingException>(errors.ThrowIfAny).Errors;
    }

    [Test]
    [Arguments("ModelOwners", "Match", "model owner curation is duplicated")]
    [Arguments("SessionAreas", "Area", "session area curation is duplicated")]
    [Arguments("OperationNames", "OperationId", null)]
    public async Task Validate_Should_Use_The_Registered_Key_Comparer(string propertyName, string key, string? problem)
    {
        var first = ContractRow(propertyName);
        var second = ContractRow(propertyName);
        second.GetType().GetProperty(key)!.SetValue(second, "SUBJECT");

        var errors = ValidateRows(typeof(GenerationCuration).GetProperty(propertyName)!, first, count: 2, secondRow: second);

        await Assert.That(errors).IsEquivalentTo(problem is null ? []
            : new[] { new BindingError(BindingErrorCategory.Curation, "SUBJECT", problem) }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments(" ")]
    public async Task Validate_Should_Refuse_An_Unsafe_Key_And_Still_Check_The_Reason(string? key)
    {
        var document = await ContractDocumentAsync();
        // In-memory invalid rows reach the contract; JSON null members are loader refusals.
        var curation = Curation(Groups("health", RootGroup()), operationNames: [OperationName(key!, "GetAsync", reason: " ")]);

        var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document, Selection("health.get"), curation));

        await Assert.That(exception.Errors.Select(static error => error.Subject + ": " + error.Problem))
            .IsEquivalentTo([
                "operationNames[0]: operationNames[0] curation names no operationId",
                "operationNames[0]: operation name curation must declare a reason",
            ], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("list", "operationNames[0]")]
    [Arguments("dictionary", "groups.health")]
    public async Task Validate_Should_Refuse_A_Null_Row(string shape, string subject)
    {
        var document = await ContractDocumentAsync();
        // Collection element nullability is not enforced by System.Text.Json.
        var curation = shape == "list" ? Curation(Groups("health", RootGroup()), operationNames: [null!])
            : Curation(Groups("health", null!));

        var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document, Selection("health.get"), curation));

        await Assert.That(exception.Errors.Single().Subject).IsEqualTo(subject);
        await Assert.That(exception.Errors.Single().Problem).IsEqualTo(subject + " curation is null");
    }
}
