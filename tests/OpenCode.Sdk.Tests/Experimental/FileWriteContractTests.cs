using System.Net;
using OpenCode.Sdk.Tests.Support;

using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Experimental;

public sealed class FileWriteContractTests
{
    [Test]
    public async Task WriteFileAsync_Should_Send_The_Bytes_With_The_Path_And_Location_Query()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.LocationEnvelope("{\"path\":\"/repo/assets/logo.bin\"}"));
        using var content = new MemoryStream([0x00, 0xFF, 0x10]);

        var response = await scenario.Client.Experimental.WriteFileAsync(
            new ExperimentalFsWriteRequest { Path = "assets/logo.bin", Location = new LocationSelector { Directory = "/repo" } },
            content);

        var recorded = scenario.Requests.Single();
        await Assert.That(recorded.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(recorded.RequestUri!.AbsoluteUri)
            .IsEqualTo("http://localhost:4096/api/experimental/fs/write?location[directory]=%2Frepo&path=assets%2Flogo.bin");
        await Assert.That(recorded.ContentType).IsEqualTo("application/octet-stream");
        await Assert.That(recorded.BodyBytes!).IsEquivalentTo(new byte[] { 0x00, 0xFF, 0x10 }, CollectionOrdering.Matching);
        await Assert.That(response.File.Path).IsEqualTo("/repo/assets/logo.bin");
        await Assert.That(response.Location.Directory).IsEqualTo(WireBodyData.ResolvedDirectory);
    }
}
