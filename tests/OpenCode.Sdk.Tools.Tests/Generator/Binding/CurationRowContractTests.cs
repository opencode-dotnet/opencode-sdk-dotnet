using System.Reflection;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

public sealed class CurationRowContractTests
{
    private static readonly Dictionary<string, string[]> KeyProblems = new(StringComparer.Ordinal)
    {
        ["Descriptions"] = ["descriptions[0] curation names no type"],
        ["SessionScopedEvent"] = [],
        ["Groups"] = ["groups[0] curation names no group"],
        ["OperationIdentities"] = ["operationIdentities[0] curation names no operationId"],
        ["OperationNames"] = ["operationNames[0] curation names no operationId"],
        ["ParameterNames"] = ["parameterNames[0] curation names no operationId", "parameterNames[0] curation names no parameter"],
        ["SchemaNames"] = ["schemaNames[0] curation names no schema"],
        ["EnvelopePayloadNames"] = ["envelopePayloadNames[0] curation names no operationId"],
        ["SchemaAliases"] = ["schemaAliases[0] curation names no schema"],
        ["TransportOwned"] = ["transportOwned[0] curation names no operationId"],
        ["HoistedMemberNames"] = ["hoistedMemberNames[0] curation names no owner", "hoistedMemberNames[0] curation names no property"],
        ["EnumMemberNames"] = ["enumMemberNames[0] curation names no schema", "enumMemberNames[0] curation names no value"],
        ["RedactedMembers"] = ["redactedMembers[0] curation names no model", "redactedMembers[0] curation names no property"],
        ["RedactedOpenMembers"] = ["redactedOpenMembers[0] curation names no model"],
        ["SecretLookingNames"] = ["secretLookingNames[0] curation names no property"],
        ["Declined"] = ["declined[0] curation names no operationId"],
        ["ModelOwners"] = ["modelOwners[0] curation names no match"],
        ["SessionAreas"] = ["sessionAreas[0] curation names no area"],
        ["QueryParameterTypes"] = ["queryParameterTypes[0] curation names no operationId", "queryParameterTypes[0] curation names no parameter"],
        ["StringQueryParameters"] = ["stringQueryParameters[0] curation names no operationId", "stringQueryParameters[0] curation names no parameter"],
    };

    public static IEnumerable<object[]> RegisteredRows() => CurationRowContract.RegisteredProperties.Select<string, object[]>(static property =>
        property switch
        {
            "Descriptions" => [property, "description curation must declare a reason", "description curation is duplicated", "Type", "subject", "descriptions[0]", KeyProblems[property]],
            "SessionScopedEvent" => [property, "session-scoped event curation must declare a reason", "session-scoped event curation is duplicated", "", "sessionScopedEvent", "sessionScopedEvent[0]", KeyProblems[property]],
            "Groups" => [property, "group curation must declare a reason", "group curation is duplicated", "group", "subject", "groups[0]", KeyProblems[property]],
            "OperationIdentities" => [property, "operation identity curation must declare a reason", "operation identity curation is duplicated", "OperationId", "subject", "operationIdentities[0]", KeyProblems[property]],
            "OperationNames" => [property, "operation name curation must declare a reason", "operation name curation is duplicated", "OperationId", "subject", "operationNames[0]", KeyProblems[property]],
            "ParameterNames" => [property, "parameter name curation must declare a reason", "parameter name curation is duplicated", "OperationId,Parameter", "subject:parameter", "parameterNames[0]", KeyProblems[property]],
            "SchemaNames" => [property, "schema name curation must declare a reason", "schema name curation is duplicated", "Schema", "subject", "schemaNames[0]", KeyProblems[property]],
            "EnvelopePayloadNames" => [property, "envelope payload name curation must declare a reason", "envelope payload name curation is duplicated", "OperationId", "subject", "envelopePayloadNames[0]", KeyProblems[property]],
            "SchemaAliases" => [property, "schema alias curation must declare a reason", "schema alias curation is duplicated", "Schema", "subject", "schemaAliases[0]", KeyProblems[property]],
            "TransportOwned" => [property, "transport-owned curation must declare a reason", "transport-owned curation is duplicated", "OperationId", "subject", "transportOwned[0]", KeyProblems[property]],
            "HoistedMemberNames" => [property, "hoisted member name curation must declare a reason", "hoisted member name curation is duplicated", "Owner,Property", "subject", "hoistedMemberNames[0]", KeyProblems[property]],
            "EnumMemberNames" => [property, "enum member name curation must declare a reason", "enum member name curation is duplicated", "Schema,Value", "subject/value", "enumMemberNames[0]", KeyProblems[property]],
            "RedactedMembers" => [property, "redacted member curation must declare a reason", "redacted member curation is duplicated", "Model,Property", "subject.property", "redactedMembers[0]", KeyProblems[property]],
            "RedactedOpenMembers" => [property, "redacted open members curation must declare a reason", "redacted open members curation is duplicated", "Model", "subject", "redactedOpenMembers[0]", KeyProblems[property]],
            "SecretLookingNames" => [property, "secret-looking name curation must declare a reason", "secret-looking name curation is duplicated", "Property", "subject", "secretLookingNames[0]", KeyProblems[property]],
            "Declined" => [property, "declined curation must declare a reason", "declined curation is duplicated", "OperationId", "subject", "declined[0]", KeyProblems[property]],
            "ModelOwners" => [property, "model owner curation must declare a reason", "model owner curation is duplicated", "Match", "subject", "modelOwners[0]", KeyProblems[property]],
            "SessionAreas" => [property, "session area curation must declare a reason", "session area curation is duplicated", "Area", "subject", "sessionAreas[0]", KeyProblems[property]],
            "QueryParameterTypes" => [property, "query parameter type curation must declare a reason", "query parameter type curation is duplicated", "OperationId,Parameter", "subject:parameter", "queryParameterTypes[0]", KeyProblems[property]],
            "StringQueryParameters" => [property, "string query parameter curation must declare a reason", "string query parameter curation is duplicated", "OperationId,Parameter", "subject:parameter", "stringQueryParameters[0]", KeyProblems[property]],
            _ => throw new InvalidOperationException("Missing contract arrangement: " + property),
        });

    [Test]
    [MethodDataSource(nameof(RegisteredRows))]
    public async Task Validate_Should_Enforce_Every_Registered_Row_Contract(string propertyName, string reasonProblem,
        string duplicateProblem, string keys, string subject, string keySubject, string[] keyProblems)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var property = typeof(GenerationCuration).GetProperty(propertyName)!;
        ArgumentNullException.ThrowIfNull(keyProblems);
        var row = ContractRow(propertyName);
        var reason = row.GetType().GetProperty("Reason")!;
        foreach (var blank in new[] { null, "", " " })
        {
            reason.SetValue(row, blank);
            var errors = ValidateRows(property, row);
            await Assert.That(errors.Single()).IsEqualTo(new BindingError(BindingErrorCategory.Curation, subject, reasonProblem));
        }

        var refusal = await BindContractRowAsync(propertyName, row);
        await Assert.That(refusal.Errors.Single(error => error.Problem == reasonProblem)).IsEqualTo(new BindingError(BindingErrorCategory.Curation, subject, reasonProblem));

        reason.SetValue(row, "Scenario row has a reviewed reason.");
        await Assert.That(ValidateRows(property, row)).IsEmpty();
        if (propertyName is not ("Groups" or "SessionScopedEvent"))
        {
            await Assert.That(ValidateRows(property, row, count: 2).Single()).IsEqualTo(new BindingError(BindingErrorCategory.Curation, subject, duplicateProblem));
        }
        var keyIndex = 0;
        foreach (var key in keys.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var field = row.GetType().GetProperty(key);
            var value = field?.GetValue(row);
            foreach (var blank in new[] { null, "", " " })
            {
                field?.SetValue(row, blank);
                var errors = ValidateRows(property, row, groupKey: blank);
                await Assert.That(errors.Single()).IsEqualTo(new BindingError(BindingErrorCategory.Curation,
                    keySubject, keyProblems[keyIndex]));
            }
            field?.SetValue(row, value);
            keyIndex++;
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
    [Arguments("Descriptions", "Type", null)]
    [Arguments("Descriptions", "Member", null, "")]
    [Arguments("Descriptions", "Member", null, "SUBJECT")]
    public async Task Validate_Should_Use_The_Registered_Key_Comparer(string propertyName, string key, string? problem, string secondValue = "SUBJECT")
    {
        var first = ContractRow(propertyName);
        var second = ContractRow(propertyName);
        second.GetType().GetProperty(key)!.SetValue(second, secondValue);

        var errors = ValidateRows(typeof(GenerationCuration).GetProperty(propertyName)!, first, count: 2, secondRow: second);

        await Assert.That(errors).IsEquivalentTo(problem is null ? []
            : new[] { new BindingError(BindingErrorCategory.Curation, "SUBJECT", problem) }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments(" ", true)]
    public async Task Validate_Should_Refuse_An_Unsafe_Key_And_Still_Check_The_Reason(string? key, bool nonzeroIndex = false)
    {
        var document = await ContractDocumentAsync();
        // In-memory invalid rows reach the contract; JSON null members are loader refusals.
        var curation = Curation(Groups("health", RootGroup()), operationNames: [OperationName(key!, "GetAsync", reason: " ")]);
        if (nonzeroIndex)
        {
            curation = curation with { OperationNames = [OperationName("health.get", "ReadHealthAsync"), .. curation.OperationNames] };
        }

        var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document, Selection("health.get"), curation));

        string[] expected = nonzeroIndex ? [
                "operationNames[1]: operationNames[1] curation names no operationId",
                "operationNames[1]: operation name curation must declare a reason",
            ] : [
                "operationNames[0]: operationNames[0] curation names no operationId",
                "operationNames[0]: operation name curation must declare a reason",
            ];
        await Assert.That(exception.Errors.Select(static error => error.Subject + ": " + error.Problem))
            .IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("list", "operationNames[0]", "operationNames[0] curation is null")]
    [Arguments("dictionary", "groups.health", "groups.health curation is null")]
    public async Task Validate_Should_Refuse_A_Null_Row(string shape, string subject, string problem)
    {
        var document = await ContractDocumentAsync();
        // Collection element nullability is not enforced by System.Text.Json.
        var curation = shape == "list" ? Curation(Groups("health", RootGroup()), operationNames: [null!])
            : Curation(Groups("health", null!));

        var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document, Selection("health.get"), curation));

        await Assert.That(exception.Errors.Single()).IsEqualTo(new BindingError(BindingErrorCategory.Curation, subject, problem));
    }
}
