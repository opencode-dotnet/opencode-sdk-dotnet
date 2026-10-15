using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Hosting.Launcher;

public sealed class ServerReadyLineTests
{
    private static class ReadyLineData
    {
        public const string Minimal = /*lang=json,strict*/ """{"url":"http://127.0.0.1:4096"}""";
        public const string WithExtraMembers = /*lang=json,strict*/ """{"url":"http://127.0.0.1:4096","pid":123}""";
        public const string NonStringUrl = /*lang=json,strict*/ """{"url":42}""";
        public const string NoUrlMember = /*lang=json,strict*/ """{"address":"http://127.0.0.1:4096"}""";
        public const string NonHttpScheme = /*lang=json,strict*/ """{"url":"ftp://127.0.0.1:4096"}""";
        public const string NotJson = "server listening on http://127.0.0.1:4096";
    }

    [Test]
    public async Task TryParse_Should_Read_The_Reference_Readiness_Line()
    {
        var parsed = ServerReadyLine.TryParse(ReadyLineData.Minimal, out var endpoint);

        await Assert.That(parsed).IsTrue();
        await Assert.That(endpoint).IsEqualTo(new Uri("http://127.0.0.1:4096"));
    }

    [Test]
    public async Task TryParse_Should_Tolerate_Extra_Members()
    {
        await Assert.That(ServerReadyLine.TryParse(ReadyLineData.WithExtraMembers, out _)).IsTrue();
    }

    public static IEnumerable<Func<string>> NonContractLines() =>
    [
        static () => ReadyLineData.NonStringUrl,
        static () => ReadyLineData.NoUrlMember,
        static () => ReadyLineData.NonHttpScheme,
        static () => ReadyLineData.NotJson,
        static () => string.Empty,
    ];

    [Test]
    [MethodDataSource(nameof(NonContractLines))]
    public async Task TryParse_Should_Refuse_A_Non_Contract_Line(string line)
    {
        await Assert.That(ServerReadyLine.TryParse(line, out _)).IsFalse();
    }
}
