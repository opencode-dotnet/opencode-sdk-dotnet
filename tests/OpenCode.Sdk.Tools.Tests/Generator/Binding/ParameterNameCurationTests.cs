using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

/// <summary>
/// The <c>parameterNames</c> curation channel: a reason-bearing row names the C# parameter one
/// route value becomes, while the wire name still fills the route. A handle family's parameter
/// names one working object, so every operation that carries it names it the same way; a row that
/// names nothing the document has refuses like every other channel.
/// </summary>
public sealed class ParameterNameCurationTests
{
    private static readonly GroupCuration ShellFamily = ClientGroup("Shells", "ShellClient", "id");

    [Test]
    public async Task Bind_Should_Name_A_Bare_Route_Value_Mechanically_Without_A_Row()
    {
        var document = await BindingTestHost.IngestAsync(Scenario());

        var plan = new BindingTestHost().Bind(document, Selection("shell.get", "shell.remove"), Curation(Groups("shell", ShellFamily)));

        await Assert.That(HandleParameter(plan).Name).IsEqualTo("id");
    }

    [Test]
    public async Task Bind_Should_Apply_A_Reasoned_Name_And_Keep_The_Wire_Name()
    {
        var document = await BindingTestHost.IngestAsync(Scenario());
        var curation = Curation(Groups("shell", ShellFamily),
            parameterNames: [ParameterName("shell.get", "id", "shellId"), ParameterName("shell.remove", "id", "shellId")]);

        var plan = new BindingTestHost().Bind(document, Selection("shell.get", "shell.remove"), curation);

        var parameter = HandleParameter(plan);
        await Assert.That(parameter.Name).IsEqualTo("shellId");
        await Assert.That(parameter.WireName).IsEqualTo("id");
    }

    [Test]
    [Arguments("shell.missing", "id", "shellId", "is not selected")]
    [Arguments("shell.get", "name", "shellId", "is not a route value")]
    [Arguments("shell.get", "id", "ShellId", "camel-cased")]
    [Arguments("shell.get", "id", "2shell", "camel-cased")]
    public async Task Bind_Should_Refuse_A_Row_That_Names_Nothing_The_Document_Has(
        string operationId, string parameter, string dotnetName, string problem)
    {
        var document = await BindingTestHost.IngestAsync(Scenario());
        var curation = Curation(Groups("shell", ShellFamily),
            parameterNames: [ParameterName(operationId, parameter, dotnetName)]);

        var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document, Selection("shell.get", "shell.remove"), curation));

        await Assert.That(exception.Errors.Any(error => error.Problem.Contains(problem, StringComparison.Ordinal)))
            .IsTrue()
            .Because(string.Join(" | ", exception.Errors.Select(static error => error.Subject + ": " + error.Problem)));
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Row_Without_A_Reason()
    {
        var document = await BindingTestHost.IngestAsync(Scenario());
        var curation = Curation(Groups("shell", ShellFamily),
            parameterNames: [ParameterName("shell.get", "id", "shellId", reason: " "), ParameterName("shell.remove", "id", "shellId")]);

        var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document, Selection("shell.get", "shell.remove"), curation));

        await Assert.That(exception.Errors.Any(static error => error.Problem.Contains("must declare a reason", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Duplicated_Row()
    {
        var document = await BindingTestHost.IngestAsync(Scenario());
        var curation = Curation(Groups("shell", ShellFamily),
            parameterNames:
            [
                ParameterName("shell.get", "id", "shellId"),
                ParameterName("shell.get", "id", "shellId"),
                ParameterName("shell.remove", "id", "shellId"),
            ]);

        var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document, Selection("shell.get", "shell.remove"), curation));

        await Assert.That(exception.Errors.Any(static error => error.Problem.Contains("is duplicated", StringComparison.Ordinal))).IsTrue();
    }

    /// <summary>A handle family that names its parameter on one operation and not the other would split one working object in two.</summary>
    [Test]
    public async Task Bind_Should_Refuse_A_Handle_Parameter_Named_On_Only_Part_Of_The_Family()
    {
        var document = await BindingTestHost.IngestAsync(Scenario());
        var curation = Curation(Groups("shell", ShellFamily), parameterNames: [ParameterName("shell.get", "id", "shellId")]);

        var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document, Selection("shell.get", "shell.remove"), curation));

        await Assert.That(exception.Errors.Any(static error => error.Problem.Contains("the same way", StringComparison.Ordinal))).IsTrue();
    }

    private static OperationParameterPlan HandleParameter(EmitPlan plan) =>
        plan.Clients.Single(static client => client.Role == ClientRole.Handle).HandleParameter!;

    /// <summary>A handle family in miniature: two operations work one shell over its bare <c>id</c>.</summary>
    private static SpecScenario Scenario() =>
        SpecScenario.Define(static spec => spec
            .WithOperation("shell.get", path: "/api/shell/{id}", configure: static operation => operation
                .Parameter("id", "path", static schema => schema.Type("string"), required: true)
                .Response(200, "application/json", static schema => schema.Type("object")))
            .WithOperation("shell.remove", method: "delete", path: "/api/shell/{id}", configure: static operation => operation
                .Parameter("id", "path", static schema => schema.Type("string"), required: true)
                .Response(200, "application/json", static schema => schema.Type("object"))));
}
