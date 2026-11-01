using System.Text;
using OpenCode.Sdk.Tools.Generator.Refresh;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.Generator.Refresh;

public sealed class DocumentInspectorTests
{
    [Test]
    public async Task Inspect_Should_Sort_Operation_Ids_And_Digest_The_Joined_Set()
    {
        var document = RefreshScenarioData.DocumentBytes(spec => spec
            .WithOperation("v2.beta.get", path: "/api/beta")
            .WithOperation("v2.alpha.get", path: "/api/alpha"));

        var stats = DocumentInspector.Inspect(document);

        await Assert.That(stats.OperationIds).IsEquivalentTo(["v2.alpha.get", "v2.beta.get"], CollectionOrdering.Matching);
        await Assert
            .That(stats.OperationSetDigest)
            .IsEqualTo(DocumentInspector.Sha256Hex(Encoding.UTF8.GetBytes("v2.alpha.get\nv2.beta.get")));
    }

    [Test]
    public async Task Inspect_Should_Count_Components_And_ContentSchema_Occurrences()
    {
        var document = RefreshScenarioData.DocumentBytes(spec => spec
            .WithOperation("v2.alpha.get", path: "/api/alpha")
            .WithSchema("Plain", schema => schema.Type("string"))
            .WithSchema("Envelope", schema => schema
                .Type("string")
                .ContentSchema("application/json", payload => payload.Ref("Plain"))));

        var stats = DocumentInspector.Inspect(document);

        await Assert.That(stats.ComponentCount).IsEqualTo(2);
        await Assert.That(stats.ContentSchemaCount).IsEqualTo(1);
    }

    [Test]
    public async Task CheckComponentKeyword_Should_Answer_All_Three_Ways()
    {
        var document = RefreshScenarioData.DocumentBytes(spec => spec
            .WithOperation("v2.alpha.get", path: "/api/alpha")
            .WithSchema("Plain", schema => schema.Type("string"))
            .WithSchema("Envelope", schema => schema
                .Type("string")
                .ContentSchema("application/json", payload => payload.Ref("Plain"))));

        await Assert
            .That(DocumentInspector.CheckComponentKeyword(document, "Envelope", "contentSchema"))
            .IsEqualTo(KeywordPresence.Carries);
        await Assert
            .That(DocumentInspector.CheckComponentKeyword(document, "Plain", "contentSchema"))
            .IsEqualTo(KeywordPresence.Lacks);
        await Assert
            .That(DocumentInspector.CheckComponentKeyword(document, "Absent", "contentSchema"))
            .IsEqualTo(KeywordPresence.ComponentMissing);
    }

    [Test]
    public async Task CheckParameterStyle_Should_Answer_All_Three_Ways()
    {
        var document = RefreshScenarioData.DocumentBytes(spec => spec
            .WithOperation("styled", configure: operation => operation.Parameter("location", "query", schema => schema.Type("object"), deepObject: true), path: "/styled")
            .WithOperation("unstyled", configure: operation => operation.Parameter("location", "query", schema => schema.Type("object")), path: "/unstyled")
            .WithOperation("otherStyle", configure: operation => operation.ParameterWithStyle("location", "form", explode: true), path: "/other")
            .WithOperation("otherCase", configure: operation => operation.Parameter("Location", "query", schema => schema.Type("object"), deepObject: true), path: "/case")
            .WithOperation("otherIn", configure: operation => operation.Parameter("location", "header", schema => schema.Type("object"), deepObject: true), path: "/header")
            .WithOperation("mixed", configure: operation => operation
                .Parameter("location", "query", schema => schema.Type("object"), deepObject: true)
                .Parameter("location", "header", schema => schema.Type("object")), path: "/mixed"));

        await Assert.That(DocumentInspector.CheckParameterStyle(document, "styled", "location", "deepObject")).IsEqualTo(KeywordPresence.Carries);
        await Assert.That(DocumentInspector.CheckParameterStyle(document, "unstyled", "location", "deepObject")).IsEqualTo(KeywordPresence.Lacks);
        await Assert.That(DocumentInspector.CheckParameterStyle(document, "otherStyle", "location", "deepObject")).IsEqualTo(KeywordPresence.Lacks);
        await Assert.That(DocumentInspector.CheckParameterStyle(document, "styled", "location", "DeepObject")).IsEqualTo(KeywordPresence.Lacks);
        await Assert.That(DocumentInspector.CheckParameterStyle(document, "styled", "absent", "deepObject")).IsEqualTo(KeywordPresence.ComponentMissing);
        await Assert.That(DocumentInspector.CheckParameterStyle(document, "absent", "location", "deepObject")).IsEqualTo(KeywordPresence.ComponentMissing);
        await Assert.That(DocumentInspector.CheckParameterStyle(document, "Styled", "location", "deepObject")).IsEqualTo(KeywordPresence.ComponentMissing);
        await Assert.That(DocumentInspector.CheckParameterStyle(document, "otherCase", "location", "deepObject")).IsEqualTo(KeywordPresence.ComponentMissing);
        await Assert.That(DocumentInspector.CheckParameterStyle(document, "otherIn", "location", "deepObject")).IsEqualTo(KeywordPresence.Carries);
        await Assert.That(DocumentInspector.CheckParameterStyle(document, "mixed", "location", "deepObject")).IsEqualTo(KeywordPresence.Lacks);
    }

    [Test]
    public async Task Inspect_Should_Refuse_Invalid_Json()
    {
        var exception = Assert.Throws<SnapshotRefreshException>(() => _ = DocumentInspector.Inspect([0x7B]));

        await Assert.That(exception.Message).Contains("not valid JSON");
    }
}
