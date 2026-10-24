using OpenCode.Sdk.Tools.Generator.Emission;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

public sealed class DocumentationSyntaxTests
{
    [Test]
    [Arguments("A <curated> & value.", "/// <summary>\n/// A &lt;curated&gt; &amp; value.\n/// </summary>\n")]
    [Arguments(" \tGets the\r\n  preserved\t raw JSON payload. \n", "/// <summary>\n/// Gets the preserved raw JSON payload.\n/// </summary>\n")]
    [Arguments("Gets the '\"<&>' wire value.", "/// <summary>\n/// Gets the &apos;&quot;&lt;&amp;&gt;&apos; wire value.\n/// </summary>\n")]
    public async Task Documentation_Should_Normalize_And_Escape_Summaries(string summary, string expected)
    {
        await Assert.That(DocumentationSyntax.Documentation(summary).ToFullString()).IsEqualTo(expected);
        await Assert.That(DocumentationSyntax.MemberDocumentation(summary, []).ToFullString()).IsEqualTo(expected);
    }

    [Test]
    public async Task MemberDocumentation_Should_Preserve_Code_Runs_In_Operation_Remarks()
    {
        DocumentationRun[] remarks =
        [
            DocumentationRun.Prose("Operation "), DocumentationRun.Code("example.list"),
            DocumentationRun.Prose(": "), DocumentationRun.Code("GET /example/{id}"), DocumentationRun.Prose("."),
        ];
        var rendered = DocumentationSyntax.MemberDocumentation("List the widgets", [], remarks: remarks).ToFullString();
        await Assert.That(rendered).IsEqualTo("/// <summary>\n/// List the widgets\n/// </summary>\n/// <remarks>\n/// Operation <c>example.list</c>: <c>GET /example/{id}</c>.\n/// </remarks>\n");
    }

    [Test]
    public async Task MemberDocumentation_Should_Render_All_Sections_And_Escape_Code()
    {
        var rendered = DocumentationSyntax.MemberDocumentation("Gets the value.",
            [new DocumentedParameter("value", " The\n wire <value>. ")], " The & result. ",
            [new DocumentedException("JsonException", " Invalid \t'value'. ")],
            [DocumentationRun.Prose("Use "), DocumentationRun.Code("\"<&>\""), DocumentationRun.Prose(" & keep  spacing.")]).ToFullString();
        await Assert.That(rendered).IsEqualTo("/// <summary>\n/// Gets the value.\n/// </summary>\n/// <param name=\"value\">The wire &lt;value&gt;.</param>\n/// <returns>The &amp; result.</returns>\n/// <exception cref=\"JsonException\">Invalid &apos;value&apos;.</exception>\n/// <remarks>\n/// Use <c>&quot;&lt;&amp;&gt;&quot;</c> &amp; keep  spacing.\n/// </remarks>\n");
    }
}
