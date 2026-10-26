using System.Text;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Output;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.Output;

public sealed class ManifestStoreTests
{
    private const string First = GenerationProvenance.FirstLine;
    private const string Second = GenerationProvenance.SecondLine;

    [Test]
    public async Task ManifestBytes_Should_Preserve_The_Ordinal_Canonical_Format()
    {
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal) { ["b"] = "B", ["A"] = "B" };

        var bytes = ManifestStore.ManifestBytes(["b.cs", "A.cs"], aliases);

        var expected = "{\n  \"files\": [\n    \"A.cs\",\n    \"b.cs\"\n  ],\n"
            + "  \"implicitAliases\": {\n    \"A\": \"B\",\n    \"b\": \"B\"\n  }\n}\n";
        await Assert.That(bytes).IsEquivalentTo(Encoding.UTF8.GetBytes(expected), CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RequireProvenanceAsync_Should_Allow_An_Absent_Entry_But_Refuse_A_Damaged_Header(bool damaged)
    {
        var fs = GenerationTestData.CreateFileSystem();
        var store = new ManifestStore(fs, new PlacementValidator(fs));
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Absent.cs", "Damaged.cs" };
        if (damaged)
        {
            await fs.File.WriteAllTextAsync(fs.Path.Combine(GenerationTestData.OutputRoot, "Damaged.cs"), "partial formatter write");
            var refusal = await Assert.That(() => store.RequireProvenanceAsync(GenerationTestData.OutputRoot, paths, CancellationToken.None))
                .Throws<OutputRefusalException>();
            await Assert.That(refusal!.Message)
                .IsEqualTo(ProvenanceRefusal("Damaged.cs"));
        }
        else
        {
            await store.RequireProvenanceAsync(GenerationTestData.OutputRoot, paths, CancellationToken.None);
        }
        await Assert.That(fs.Directory.EnumerateFiles(GenerationTestData.OutputRoot)).Count().IsEqualTo(damaged ? 2 : 1);
    }

    [Test]
    [Arguments("UTF-8 without BOM", "utf-8", false, First + "\n" + Second + "\n", new byte[] { }, new byte[] { }, true)]
    [Arguments("UTF-8 BOM", "utf-8", true, First + "\n" + Second + "\n", new byte[] { }, new byte[] { }, true)]
    [Arguments("UTF-16 LE BOM", "utf-16", true, First + "\n" + Second + "\n", new byte[] { }, new byte[] { }, true)]
    [Arguments("UTF-16 BE BOM", "utf-16BE", true, First + "\n" + Second + "\n", new byte[] { }, new byte[] { }, true)]
    [Arguments("UTF-32 LE BOM", "utf-32", true, First + "\n" + Second + "\n", new byte[] { }, new byte[] { }, true)]
    [Arguments("UTF-32 BE BOM", "utf-32BE", true, First + "\n" + Second + "\n", new byte[] { }, new byte[] { }, true)]
    [Arguments("UTF-16 without BOM", "utf-16", false, First + "\n" + Second + "\n", new byte[] { }, new byte[] { }, false)]
    [Arguments("extra BOM", "utf-8", true, "\uFEFF" + First + "\n" + Second + "\n", new byte[] { }, new byte[] { }, false)]
    [Arguments("repeated CR", "utf-8", false, First + "\r\r\n" + Second + "\r\r\nbody", new byte[] { }, new byte[] { }, true)]
    [Arguments("no final LF", "utf-8", false, First + "\n" + Second + "\r\r", new byte[] { }, new byte[] { }, true)]
    [Arguments("lone CR separators", "utf-8", false, First + "\r" + Second + "\r", new byte[] { }, new byte[] { }, false)]
    [Arguments("damaged second line", "utf-8", false, First + "\n// Do not edit by hand.\n", new byte[] { }, new byte[] { }, false)]
    [Arguments("invalid byte after header", "utf-8", false, First + "\n" + Second + "\n", new byte[] { }, new byte[] { 0xFF }, true)]
    [Arguments("invalid byte before header", "utf-8", false, First + "\n" + Second + "\n", new byte[] { 0xFF }, new byte[] { }, false)]
    public async Task RequireProvenanceAsync_Should_Preserve_Bom_And_Line_Semantics(
        string label, string encodingName, bool withBom, string content, byte[] prefix, byte[] suffix, bool admitted)
    {
        const string entry = "Entry.cs";
        var fs = GenerationTestData.CreateFileSystem();
        var store = new ManifestStore(fs, new PlacementValidator(fs));
        var encoding = Encoding.GetEncoding(encodingName);
        var preamble = withBom ? encoding.GetPreamble() : [];
        byte[] bytes = [.. prefix, .. preamble, .. encoding.GetBytes(content), .. suffix];
        var path = fs.Path.Combine(GenerationTestData.OutputRoot, entry);
        await fs.File.WriteAllBytesAsync(path, bytes);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { entry };

        if (admitted)
        {
            await Assert.That(() => store.RequireProvenanceAsync(GenerationTestData.OutputRoot, paths, CancellationToken.None))
                .ThrowsNothing().Because(label);
        }
        else
        {
            var refusal = await Assert.That(() => store.RequireProvenanceAsync(GenerationTestData.OutputRoot, paths, CancellationToken.None))
                .Throws<OutputRefusalException>().Because(label);
            await Assert.That(refusal!.Message)
                .IsEqualTo(ProvenanceRefusal(entry)).Because(label);
        }
    }

    private static string ProvenanceRefusal(string entry) =>
        $"Manifest entry '{entry}' does not carry the provenance header; refusing to overwrite or delete it.";
}
