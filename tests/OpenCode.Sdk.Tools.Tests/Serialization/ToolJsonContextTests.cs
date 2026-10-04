using System.Text;
using System.Text.Json;
using OpenCode.Sdk.Tools.Generator.Refresh.Models;
using OpenCode.Sdk.Tools.Output;
using OpenCode.Sdk.Tools.Serialization;
using Testably.Abstractions.Testing;

namespace OpenCode.Sdk.Tools.Tests.Serialization;

public sealed class ToolJsonContextTests
{
    private const string ReceiptPath = "receipt.json";
    private const string ManifestPath = "manifest.json";

    [Test]
    public async Task SnapshotReceipt_Should_Write_With_No_Carriage_Returns_And_One_Trailing_Newline()
    {
        var fileSystem = new MockFileSystem();
        var content = $"{JsonSerializer.Serialize(SampleReceipt(), ToolJsonContext.Default.SnapshotReceipt)}\n";
        await fileSystem.File.WriteAllBytesAsync(ReceiptPath, Encoding.UTF8.GetBytes(content), CancellationToken.None);

        var bytes = await fileSystem.File.ReadAllBytesAsync(ReceiptPath, CancellationToken.None);

        await AssertNoCarriageReturnAndOneTrailingNewline(bytes);
    }

    [Test]
    public async Task GenerationManifest_Should_Write_With_No_Carriage_Returns_And_One_Trailing_Newline()
    {
        var fileSystem = new MockFileSystem();
        var manifest = new GenerationManifest { Files = ["Models/Widget.cs"] };
        var content = $"{JsonSerializer.Serialize(manifest, ToolJsonContext.Default.GenerationManifest)}\n";
        await fileSystem.File.WriteAllBytesAsync(ManifestPath, Encoding.UTF8.GetBytes(content), CancellationToken.None);

        var bytes = await fileSystem.File.ReadAllBytesAsync(ManifestPath, CancellationToken.None);

        await AssertNoCarriageReturnAndOneTrailingNewline(bytes);
    }

    private static async Task AssertNoCarriageReturnAndOneTrailingNewline(byte[] bytes)
    {
        await Assert.That(Array.IndexOf(bytes, (byte)'\r')).IsEqualTo(-1);
        await Assert.That(bytes[^1]).IsEqualTo((byte)'\n');
        await Assert.That(bytes[^2]).IsNotEqualTo((byte)'\n');
    }

    private static SnapshotReceipt SampleReceipt() =>
        new()
        {
            SchemaVersion = 1,
            UpstreamCommit = "0123456789abcdef0123456789abcdef01234567",
            RawDocumentSha256 = "aa",
            GeneratedBaselineSha256 = "aa",
            Patches = [],
            NormalizedDocumentSha256 = "bb",
            NormalizedDocumentPath = null,
            OperationSetDigest = "cc",
            OperationCount = 0,
            AddedOperations = [],
            RemovedOperations = [],
            ComponentCount = 0,
            ContentSchemaCount = 0,
        };
}
