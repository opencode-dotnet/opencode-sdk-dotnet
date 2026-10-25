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
    [Arguments("utf-8", false, First + "\n" + Second + "\n", true)]
    [Arguments("utf-8", true, First + "\n" + Second + "\n", true)]
    [Arguments("utf-16", true, First + "\n" + Second + "\n", true)]
    [Arguments("utf-16BE", true, First + "\n" + Second + "\n", true)]
    [Arguments("utf-32", true, First + "\n" + Second + "\n", true)]
    [Arguments("utf-32BE", true, First + "\n" + Second + "\n", true)]
    [Arguments("utf-16", false, First + "\n" + Second + "\n", false)]
    [Arguments("utf-8", true, "\uFEFF" + First + "\n" + Second + "\n", false)]
    [Arguments("utf-8", false, First + "\r\r\n" + Second + "\r\r\nbody", true)]
    [Arguments("utf-8", false, First + "\n" + Second + "\r\r", true)]
    [Arguments("utf-8", false, First + "\r" + Second + "\r", false)]
    [Arguments("utf-8", false, First + "\n// Do not edit by hand.\n", false)]
    public async Task RequireProvenanceAsync_Should_Preserve_Bom_And_Line_Semantics(
        string encodingName, bool withBom, string content, bool admitted)
    {
        const string entry = "Entry.cs";
        var fs = GenerationTestData.CreateFileSystem();
        var store = new ManifestStore(fs, new PlacementValidator(fs));
        var encoding = Encoding.GetEncoding(encodingName);
        var preamble = withBom ? encoding.GetPreamble() : [];
        var bytes = preamble.Concat(encoding.GetBytes(content)).ToArray();
        var path = fs.Path.Combine(GenerationTestData.OutputRoot, entry);
        await fs.File.WriteAllBytesAsync(path, bytes);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { entry };

        if (admitted)
        {
            await store.RequireProvenanceAsync(GenerationTestData.OutputRoot, paths, CancellationToken.None);
        }
        else
        {
            var refusal = await Assert.That(() => store.RequireProvenanceAsync(GenerationTestData.OutputRoot, paths, CancellationToken.None))
                .Throws<OutputRefusalException>();
            await Assert.That(refusal!.Message)
                .IsEqualTo("Manifest entry 'Entry.cs' does not carry the provenance header; refusing to overwrite or delete it.");
        }

        await Assert.That(await fs.File.ReadAllBytesAsync(path)).IsEquivalentTo(bytes, CollectionOrdering.Matching);
    }

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
                .IsEqualTo("Manifest entry 'Damaged.cs' does not carry the provenance header; refusing to overwrite or delete it.");
        }
        else
        {
            await store.RequireProvenanceAsync(GenerationTestData.OutputRoot, paths, CancellationToken.None);
        }
        await Assert.That(fs.Directory.EnumerateFiles(GenerationTestData.OutputRoot)).Count().IsEqualTo(damaged ? 2 : 1);
    }
}
