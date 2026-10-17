using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenCode.Sdk.Tests.Common.Support;
using OpenCode.Sdk.Tests.Config;
using OpenCode.Sdk.Tests.Credentials;
using OpenCode.Sdk.Tests.Debug;
using OpenCode.Sdk.Tests.Experimental;
using OpenCode.Sdk.Tests.FileSystem;
using OpenCode.Sdk.Tests.Forms;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Integrations;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.Tests.Worktrees;

namespace OpenCode.Sdk.Tests.Common.Errors;

public sealed class DeclaredErrorTests
{
    public static IEnumerable<Func<DeclaredErrorCase>> EveryDoor() =>
    [
        .. WorktreesDeclaredErrors.Rows(),
        .. ConfigDeclaredErrors.Rows(),
        .. CredentialsDeclaredErrors.Rows(),
        .. DebugDeclaredErrors.Rows(),
        .. ExperimentalDeclaredErrors.Rows(),
        .. FormsDeclaredErrors.Rows(),
        .. FileSystemDeclaredErrors.Rows(),
        .. IntegrationsDeclaredErrors.Rows(),
    ];

    [Test]
    [MethodDataSource(nameof(EveryDoor))]
    [DisplayName(nameof(Door_Should_Throw_The_Declared_Error) + "($row)")]
    public async Task Door_Should_Throw_The_Declared_Error(DeclaredErrorCase row)
    {
        using var scenario = ContractScenario.Responding(row.Status, row.WireBody);

        var exception = await Assert.That(async () => _ = await row.Door.Invoke(scenario.Client, null))
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo((int)row.Status);
        await Assert.That(exception.Error?.GetType()).IsEqualTo(row.ErrorType);
    }

    [Test]
    [MethodDataSource(nameof(EveryDoor))]
    [DisplayName(nameof(Door_Should_Return_The_Declared_Error_On_NoThrow) + "($row)")]
    public async Task Door_Should_Return_The_Declared_Error_On_NoThrow(DeclaredErrorCase row)
    {
        using var scenario = ContractScenario.Responding(row.Status, row.WireBody);

        var response = await row.Door.Invoke(scenario.Client, OpenCodeRequestOptions.NoThrow);

        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.Status).IsEqualTo((int)row.Status);
        await Assert.That(response.Error?.GetType()).IsEqualTo(row.ErrorType);
    }

    [Test]
    [MethodDataSource(nameof(EveryDoor))]
    [DisplayName(nameof(Every_Row_Should_Match_The_Pinned_Declaration) + "($row)")]
    public async Task Every_Row_Should_Match_The_Pinned_Declaration(DeclaredErrorCase row)
    {
        using var document = JsonDocument.Parse(new FixtureLoader().LoadJson("Pinned.openapi.json"));
        var operation = document.RootElement.GetProperty("paths").GetProperty(row.Door.Path)
            .GetProperty(row.Door.HttpMethod);
        await Assert.That(operation.GetProperty("operationId").GetString()).IsEqualTo(row.Door.OperationId);

        var operationPointer = "#/paths/" + new StringBuilder(row.Door.Path).Replace("~", "~0").Replace("/", "~1")
            + "/" + row.Door.HttpMethod + "/responses/";
        await Assert.That(row.JsonPointer).StartsWith(operationPointer);
        var status = row.JsonPointer[operationPointer.Length..].Split('/')[0];
        await Assert.That(status).IsEqualTo(((int)row.Status).ToString(CultureInfo.InvariantCulture));

        var branch = ResolvePointer(document.RootElement, row.JsonPointer);
        // GetProperty throws when the branch has no $ref, and a pinned $ref value is a JSON string, so GetString() is not null.
        var reference = branch.GetProperty("$ref").GetString()!;
        await Assert.That(reference).StartsWith("#/components/schemas/");
        var component = ResolvePointer(document.RootElement, reference);
        var tags = component.GetProperty("properties").GetProperty("_tag").GetProperty("enum");
        await Assert.That(tags.GetArrayLength()).IsEqualTo(1);
        await Assert.That(tags[0].GetString()).IsEqualTo(row.Tag);
    }

    private static JsonElement ResolvePointer(JsonElement root, string pointer)
    {
        var current = root;
        foreach (var segment in pointer.Split('/').Skip(1))
        {
            var key = new StringBuilder(segment).Replace("~1", "/").Replace("~0", "~").ToString();
            current = current.ValueKind == JsonValueKind.Array
                ? current[int.Parse(key, CultureInfo.InvariantCulture)]
                : current.GetProperty(key);
        }

        return current;
    }
}
