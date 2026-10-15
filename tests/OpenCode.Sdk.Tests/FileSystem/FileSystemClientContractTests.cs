using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.FileSystem;

public sealed class FileSystemClientContractTests
{
    [Test]
    public async Task ListEntriesAsync_Should_Return_The_Typed_Entries_With_Their_Location()
    {
        const string entries = "[{\"path\":\"src\",\"type\":\"directory\"},{\"path\":\"README.md\",\"type\":\"file\"}]";
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.LocationEnvelope(entries));

        var response = await scenario.Client.FileSystem.ListEntriesAsync();

        await Assert.That(response.Entries.Count).IsEqualTo(2);
        await Assert.That(response.Entries[0].Path).IsEqualTo("src");
        await Assert.That(response.Entries[0].Type).IsEqualTo(FileSystemEntryType.Directory);
        await Assert.That(response.Entries[1].Path).IsEqualTo("README.md");
        await Assert.That(response.Entries[1].Type).IsEqualTo(FileSystemEntryType.File);
        await Assert.That(response.Location.Directory).IsEqualTo(WireBodyData.ResolvedDirectory);
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/fs/list"));
    }

    [Test]
    public async Task ListEntriesAsync_Should_Return_An_Empty_List_With_Its_Location()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.LocationEnvelope("[]"));

        var response = await scenario.Client.FileSystem.ListEntriesAsync();

        await Assert.That(response.Entries.Count).IsEqualTo(0);
        await Assert.That(response.Location.Directory).IsEqualTo(WireBodyData.ResolvedDirectory);
    }

    [Test]
    public async Task ListEntriesAsync_Should_Send_The_Path_And_Location_Query()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.LocationEnvelope("[]"));

        _ = await scenario.Client.FileSystem.ListEntriesAsync(new FsListRequest
        {
            Path = "src",
            Location = new LocationSelector { Directory = "/repo" },
        });

        await Assert.That(scenario.Requests.Single().RequestUri!.AbsoluteUri)
            .IsEqualTo("http://localhost:4096/api/fs/list?location[directory]=%2Frepo&path=src");
    }

    [Test]
    public async Task ListEntriesAsync_Should_Throw_The_Declared_400_Error()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.BadRequest, WireBodyData.InvalidRequestError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.FileSystem.ListEntriesAsync())
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(400);
        await Assert.That(exception.Error).IsTypeOf<InvalidRequestError>();
    }

    [Test]
    public async Task ListEntriesAsync_Should_Return_The_401_Error_On_The_NoThrow_Spine()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.Unauthorized, WireBodyData.UnauthorizedError);

        var response = await scenario.Client.FileSystem.ListEntriesAsync(requestOptions: OpenCodeRequestOptions.NoThrow);

        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.Status).IsEqualTo(401);
        await Assert.That(response.Error).IsTypeOf<UnauthorizedError>();
    }

    [Test]
    public async Task FindEntriesAsync_Should_Return_The_Typed_Entries_With_Their_Location()
    {
        using var scenario = ContractScenario.Responding(
            HttpStatusCode.OK, WireBodyData.LocationEnvelope($"[{WireBodyData.FileSystemEntry}]"));

        var response = await scenario.Client.FileSystem.FindEntriesAsync(new FsFindRequest { Query = "todo" });

        await Assert.That(response.Entries.Count).IsEqualTo(1);
        await Assert.That(response.Entries[0].Path).IsEqualTo("src/App.cs");
        await Assert.That(response.Entries[0].Type).IsEqualTo(FileSystemEntryType.File);
        await Assert.That(response.Location.Directory).IsEqualTo(WireBodyData.ResolvedDirectory);
        await Assert.That(scenario.Requests.Single().RequestUri!.AbsoluteUri)
            .IsEqualTo("http://localhost:4096/api/fs/find?query=todo");
    }

    [Test]
    public async Task FindEntriesAsync_Should_Send_The_Enum_Type_As_Its_Wire_Value()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.LocationEnvelope("[]"));

        _ = await scenario.Client.FileSystem.FindEntriesAsync(new FsFindRequest
        {
            Query = "todo",
            Type = FsFindRequestType.Directory,
            Limit = 10,
        });

        await Assert.That(scenario.Requests.Single().RequestUri!.AbsoluteUri)
            .IsEqualTo("http://localhost:4096/api/fs/find?query=todo&type=directory&limit=10");
    }

    [Test]
    public async Task FindEntriesAsync_Should_Throw_The_Declared_400_Error()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.BadRequest, WireBodyData.InvalidRequestError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.FileSystem.FindEntriesAsync(new FsFindRequest { Query = "todo" }))
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(400);
        await Assert.That(exception.Error).IsTypeOf<InvalidRequestError>();
    }

    [Test]
    public async Task FindEntriesAsync_Should_Return_The_401_Error_On_The_NoThrow_Spine()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.Unauthorized, WireBodyData.UnauthorizedError);

        var response = await scenario.Client.FileSystem.FindEntriesAsync(
            new FsFindRequest { Query = "todo" },
            OpenCodeRequestOptions.NoThrow);

        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.Status).IsEqualTo(401);
        await Assert.That(response.Error).IsTypeOf<UnauthorizedError>();
    }

    /// <summary>
    /// A file is raw bytes, not text: bytes that are not valid UTF-8 come back unchanged, beside
    /// the Content-Type the server declared from the file's extension.
    /// </summary>
    [Test]
    public async Task ReadFileAsync_Should_Return_The_Raw_Bytes_And_Their_Content_Type()
    {
        byte[] bytes = [0x89, 0x50, 0x4E, 0x47, 0xFF, 0xFE, 0x00, 0x0A];
        using var scenario = ContractScenario.Responding(_ =>
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var response = await scenario.Client.FileSystem.ReadFileAsync(new FsReadRequest { Path = "assets/logo.png" });

        await Assert.That(response.Status).IsEqualTo(200);
        await Assert.That(response.Content.ToArray()).IsEquivalentTo(bytes);
        await Assert.That(response.ContentType).IsEqualTo("image/png");
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/fs/read/assets/logo.png"));
    }

    [Test]
    public async Task ReadFileAsync_Should_Escape_Each_Path_Segment_And_Send_The_Location()
    {
        using var scenario = ContractScenario.Responding(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) });

        _ = await scenario.Client.FileSystem.ReadFileAsync(new FsReadRequest
        {
            Path = "src/my file #1.ts",
            Location = new LocationSelector { Directory = "/repo" },
        });

        await Assert.That(scenario.Requests.Single().RequestUri!.AbsoluteUri)
            .IsEqualTo("http://localhost:4096/api/fs/read/src/my%20file%20%231.ts?location[directory]=%2Frepo");
    }

    [Test]
    [Arguments("..")]
    [Arguments(".")]
    [Arguments("src/../secret.txt")]
    [Arguments("./README.md")]
    public async Task ReadFileAsync_Should_Refuse_A_Dot_Segment_Before_Sending(string path)
    {
        using var scenario = ContractScenario.Responding(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) });

        _ = await Assert
            .That(async () => _ = await scenario.Client.FileSystem.ReadFileAsync(new FsReadRequest { Path = path }))
            .Throws<ArgumentException>();
        await Assert.That(scenario.Requests).IsEmpty();
    }

    [Test]
    public async Task ReadFileAsync_Should_Throw_The_Declared_404_File_Not_Found_Error()
    {
        using var scenario = ContractScenario.Responding(
            HttpStatusCode.NotFound,
            "{\"_tag\":\"FileNotFoundError\",\"path\":\"missing.txt\",\"message\":\"File not found: missing.txt\"}");

        var exception = await Assert
            .That(async () => _ = await scenario.Client.FileSystem.ReadFileAsync(new FsReadRequest { Path = "missing.txt" }))
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(404);
        await Assert.That(exception.Error).IsTypeOf<FileNotFoundError>();
        await Assert.That(((FileNotFoundError)exception.Error!).Path).IsEqualTo("missing.txt");
    }
}
