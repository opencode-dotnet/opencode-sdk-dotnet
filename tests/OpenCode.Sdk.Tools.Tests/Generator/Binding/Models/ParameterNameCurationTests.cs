using System.Text;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Support;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding.Models;

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
    [Arguments("shell.missing", "id", "shellId", BindingErrorCategory.Curation, "shell.missing:id", "curated operation is not selected by the current profile")]
    [Arguments("shell.get", "name", "shellId", BindingErrorCategory.Curation, "shell.get:name", "curated parameter is not a route value of the operation")]
    [Arguments("shell.get", "id", "ShellId", BindingErrorCategory.Naming, "shell.get:id", "parameter name 'ShellId' must be a valid camel-cased C# identifier")]
    [Arguments("shell.get", "id", "2shell", BindingErrorCategory.Naming, "shell.get:id", "parameter name '2shell' must be a valid camel-cased C# identifier")]
    [Arguments("shell.get", "id", "class", BindingErrorCategory.Naming, "shell.get:id", "parameter name 'class' must be a valid camel-cased C# identifier")]
    [Arguments("shell.get", "id", "id", BindingErrorCategory.Curation, "shell.get:id", "parameter name 'id' changes no name; the mechanical rule already gives it")]
    [Arguments("shell.get", "id", "shellId", BindingErrorCategory.Curation, "Shells", "every operation of the handle family must name handle parameter 'id' the same way")]
    [Arguments("flat", "first", "second", BindingErrorCategory.Naming, "shell.get", "multiple parameters map to C# name 'second'")]
    [Arguments("flat", "first", "cancellationToken", BindingErrorCategory.Naming, "shell.get", "parameter name 'cancellationToken' is reserved by the emitted method signature")]
    public async Task Bind_Should_Refuse_Invalid_Parameter_Name_Rows(
        string operationId, string parameter, string dotnetName, BindingErrorCategory category, string subject, string problem)
    {
        var scenario = operationId == "flat" ? SpecScenario.Define(static spec => spec.WithOperation("shell.get", path: "/api/shell/{first}/{second}",
            configure: static operation => operation.Parameter("first", "path", static schema => schema.Type("string"), required: true)
                .Parameter("second", "path", static schema => schema.Type("string"), required: true))) : Scenario();
        var document = await BindingTestHost.IngestAsync(scenario);
        var group = operationId == "flat" ? ClientGroup("Shells", handleName: null, handleParameter: null) : ShellFamily;
        var curation = Curation(Groups("shell", group),
            parameterNames: [ParameterName(operationId == "flat" ? "shell.get" : operationId, parameter, dotnetName)]);

        var selection = operationId == "flat" ? Selection("shell.get") : Selection("shell.get", "shell.remove");
        var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document, selection, curation));

        await Assert.That(exception.Errors.Single(error => error.Category == category && error.Subject == subject))
            .IsEqualTo(new BindingError(category, subject, problem));
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Row_Without_A_Reason()
    {
        var document = await BindingTestHost.IngestAsync(Scenario());
        var curation = Curation(Groups("shell", ShellFamily),
            parameterNames: [ParameterName("shell.get", "id", "shellId", reason: " "), ParameterName("shell.remove", "id", "shellId")]);

        var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document, Selection("shell.get", "shell.remove"), curation));

        await Assert.That(exception.Errors).Contains(static error => error.Problem.Contains("must declare a reason", StringComparison.Ordinal));
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

        await Assert.That(exception.Errors).Contains(static error => error.Problem.Contains("is duplicated", StringComparison.Ordinal));
    }

    /// <summary>A handle family that names its parameter on one operation and not the other would split one working object in two.</summary>
    [Test]
    public async Task Bind_Should_Refuse_A_Handle_Parameter_Named_On_Only_Part_Of_The_Family()
    {
        var document = await BindingTestHost.IngestAsync(Scenario());
        var curation = Curation(Groups("shell", ShellFamily), parameterNames: [ParameterName("shell.get", "id", "shellId")]);

        var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document, Selection("shell.get", "shell.remove"), curation));

        await Assert.That(exception.Errors).Contains(static error => error.Problem.Contains("the same way", StringComparison.Ordinal));
    }

    /// <summary>A row that spells what the mechanical rule already gives renames nothing, so it would only hide a stale intent.</summary>
    [Test]
    public async Task Bind_Should_Refuse_A_Row_That_Repeats_The_Mechanical_Name()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(static spec => spec
            .WithOperation("shell.get", path: "/api/shell/{shellID}", configure: static operation => operation
                .Parameter("shellID", "path", static schema => schema.Type("string"), required: true)
                .Response(200, "application/json", static schema => schema.Type("object")))));
        var curation = Curation(Groups("shell", ClientGroup("Shells", "ShellClient", "shellID")),
            parameterNames: [ParameterName("shell.get", "shellID", "shellId")]);

        var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document, Selection("shell.get"), curation));

        await Assert.That(exception.Errors).Contains(static error => error.Problem.Contains("changes no name", StringComparison.Ordinal));
    }

    /// <summary>
    /// Two wire groups that share a client name are one family with one handle, so a row that names
    /// the handle parameter in one group and not the other would split it, even though each group
    /// agrees with itself.
    /// </summary>
    [Test]
    public async Task Bind_Should_Refuse_A_Handle_Parameter_Named_In_Only_One_Group_Of_A_Merged_Family()
    {
        var document = await BindingTestHost.IngestAsync(MergedScenario());
        var curation = Curation(
            new Dictionary<string, GroupCuration>(StringComparer.Ordinal) { ["shell"] = ShellFamily, ["terminal"] = ShellFamily },
            parameterNames: [ParameterName("shell.get", "id", "shellId"), ParameterName("shell.remove", "id", "shellId")]);

        var exception = Assert.Throws<BindingException>(() =>
            new BindingTestHost().Bind(document, Selection("shell.get", "shell.remove", "terminal.get"), curation));

        await Assert.That(exception.Errors).Contains(static error => error.Subject == "Shells"
                                                              && error.Problem.Contains("the same way", StringComparison.Ordinal));
    }

    /// <summary>A flat operation's renamed route value reaches its method signature, its parameter documentation and its route builder, while the template keeps the wire name.</summary>
    [Test]
    public async Task Emit_Should_Use_The_Curated_Name_On_A_Flat_Operation()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(static spec => spec
            .WithOperation("permission.saved.remove", method: "delete", path: "/api/permission/saved/{id}", configure: static operation => operation
                .Parameter("id", "path", static schema => schema.Type("string"), required: true)
                .Response(200, "application/json", static schema => schema.Type("object")))));
        var curation = Curation(Groups("permission", ClientGroup("Permissions", handleName: null, handleParameter: null)),
            parameterNames: [ParameterName("permission.saved.remove", "id", "savedPermissionId")]);

        var plan = new BindingTestHost().Bind(document, Selection("permission.saved.remove"), curation);

        var sources = SourceEmitter.Emit(plan);
        var client = Emitted(sources, "/PermissionsClient.cs");
        await Assert.That(client).Contains("(string savedPermissionId,");
        await Assert.That(client).Contains("<param name = \"savedPermissionId\">");
        await Assert.That(client).DoesNotContain("string id");
        var routes = Emitted(sources, "/OpenCodeRoutes.Permissions.cs");
        await Assert.That(routes).Contains("string savedPermissionId");
        await Assert.That(routes).Contains("/api/permission/saved/{id}");
    }

    /// <summary>The repository's own rows: the shell handle and the saved-permission id read descriptively, and the wire keeps <c>id</c>.</summary>
    [Test]
    public async Task Bind_Should_Apply_The_Repository_Rows_To_The_Pinned_Document()
    {
        var plan = await new BindingTestHost().BindPinnedAsync();

        var shell = plan.Clients.Single(static client => client is { Role: ClientRole.Handle, Name: "ShellClient" }).HandleParameter!;
        await Assert.That(shell.Name).IsEqualTo("shellId");
        await Assert.That(shell.WireName).IsEqualTo("id");
        var saved = plan.Clients
            .SelectMany(static client => client.Operations)
            .Single(static operation => operation.OperationId == "permission.saved.remove")
            .Parameters.Single(static parameter => parameter.WireName == "id");
        await Assert.That(saved.Name).IsEqualTo("savedPermissionId");
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

    /// <summary>The shell family merged from two wire groups: a terminal operation works the same shell.</summary>
    private static SpecScenario MergedScenario() =>
        SpecScenario.Define(static spec => spec
            .WithOperation("shell.get", path: "/api/shell/{id}", configure: static operation => operation
                .Parameter("id", "path", static schema => schema.Type("string"), required: true)
                .Response(200, "application/json", static schema => schema.Type("object")))
            .WithOperation("shell.remove", method: "delete", path: "/api/shell/{id}", configure: static operation => operation
                .Parameter("id", "path", static schema => schema.Type("string"), required: true)
                .Response(200, "application/json", static schema => schema.Type("object")))
            .WithOperation("terminal.get", path: "/api/terminal/{id}", configure: static operation => operation
                .Parameter("id", "path", static schema => schema.Type("string"), required: true)
                .Response(200, "application/json", static schema => schema.Type("object"))));

    private static string Emitted(IReadOnlyList<GeneratedSource> sources, string suffix) =>
        Encoding.UTF8.GetString(sources.Single(source => source.RelativePath.EndsWith(suffix, StringComparison.Ordinal)).Utf8Source.Span);
}
