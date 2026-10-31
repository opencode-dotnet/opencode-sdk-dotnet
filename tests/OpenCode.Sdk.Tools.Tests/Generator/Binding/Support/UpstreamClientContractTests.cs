using System.Globalization;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding.Support;

/// <summary>
/// The grammar <see cref="UpstreamClientContract"/> reads from upstream's generated client: a route
/// and its query keys, the input types' top-level fields, and the classification of a field's type
/// text. A type outside the grammar reads as unknown, never as a guess, so the oracle refuses it.
/// </summary>
public sealed class UpstreamClientContractTests
{
    private const string Client = """
        export function createClient() {
          return {
            vcs: {
              diff: (input: VcsDiffInput, requestOptions?: RequestOptions) =>
                request<VcsDiffOutput>(
                  {
                    method: "GET",
                    path: `/api/vcs/diff`,
                    query: { mode: input["mode"], "context-lines": input["context"] },
                    successStatus: 200,
                  },
                  requestOptions,
                ),
            },
            session: {
              log: (input: SessionLogInput, requestOptions?: RequestOptions): AsyncIterable<SessionLogOutput> =>
                sse<SessionLogOutput>(
                  {
                    method: "GET",
                    path: `/api/session/${encodeURIComponent(input.sessionID)}/log`,
                    query: { after: input["after"] },
                  },
                  requestOptions,
                ),
            },
            file: {
              read: (input: FileReadInput, requestOptions?: RequestOptions) =>
                request<FileReadOutput>(
                  {
                    method: "GET",
                    path: `/api/fs/read/${encodePath(input.path)}`,
                    query: { location: input["location"] },
                  },
                  requestOptions,
                ),
            },
          }
        }
        """;

    private const string Api = """
        export type VcsDiffInput = {
          readonly mode: Vcs.Mode
          readonly context?: number | undefined
          readonly note?: "a \" quote, a { brace and readonly text" | undefined
        }
        export type SessionLogInput = { readonly sessionID: Session.ID; readonly after?: Event.Seq | undefined }
        """;

    private const string PromiseTypes = """
        export type FileReadInput = {
          readonly location?: { readonly location?: { readonly directory?: string | undefined } | undefined }["location"]
        }
        export type VcsDiffInput = {
          readonly context?: string | undefined
        }
        """;

    [Test]
    [Arguments("GET", "/api/vcs/diff", "context-lines", "number")]
    [Arguments("GET", "/api/vcs/diff", "mode", "Vcs.Mode")]
    [Arguments("GET", "/api/session/{sessionID}/log", "after", "Event.Seq")]
    [Arguments("GET", "/api/fs/read/*", "location", "{ readonly location?: { readonly directory?: string } }[\"location\"]")]
    public async Task TryGetQueryType_Should_Read_The_Field_A_Query_Key_Takes(string method, string path, string key, string expected)
    {
        var contract = UpstreamClientContract.Parse(Client, Api, PromiseTypes);

        var found = contract.TryGetQueryType(method, path, key, out var type, out var problem);

        await Assert.That(found).IsTrue().Because(problem);
        await Assert.That(type).IsEqualTo(expected);
    }

    /// <summary>The first source wins a name both declare, so the effect client's input stands over the promise client's.</summary>
    [Test]
    public async Task Parse_Should_Prefer_The_First_Source_That_Declares_An_Input()
    {
        var contract = UpstreamClientContract.Parse(Client, Api, PromiseTypes);

        _ = contract.TryGetQueryType("GET", "/api/vcs/diff", "context-lines", out var type, out _);

        await Assert.That(type).IsEqualTo("number");
    }

    /// <summary>A signature the upstream printer wraps across lines, with a trailing comma, still declares its route.</summary>
    [Test]
    public async Task Parse_Should_Read_A_Route_Whose_Signature_Wraps_Across_Lines()
    {
        var contract = UpstreamClientContract.Parse(
            Client.Replace(
                "log: (input: SessionLogInput, requestOptions?: RequestOptions): AsyncIterable<SessionLogOutput> =>",
                "log: (\n  input: SessionLogInput,\n  requestOptions?: RequestOptions,\n): AsyncIterable<SessionLogOutput> =>",
                StringComparison.Ordinal),
            Api,
            PromiseTypes);

        var found = contract.TryGetQueryType("GET", "/api/session/{sessionID}/log", "after", out var type, out var problem);

        await Assert.That(found).IsTrue().Because(problem);
        await Assert.That(type).IsEqualTo("Event.Seq");
    }

    /// <summary>Quotes, braces and the word readonly inside a string literal do not split or end a field.</summary>
    [Test]
    public async Task Parse_Should_Keep_A_Literal_With_Escaped_Quotes_And_Braces_Whole()
    {
        var contract = UpstreamClientContract.Parse(
            Client.Replace("\"context-lines\": input[\"context\"]", "note: input[\"note\"]", StringComparison.Ordinal), Api);

        _ = contract.TryGetQueryType("GET", "/api/vcs/diff", "note", out var type, out var problem);

        await Assert.That(type).IsEqualTo("\"a \\\" quote, a { brace and readonly text\"").Because(problem);
    }

    /// <summary>
    /// A client forty times the pinned client's size still reads its first and last route: matching
    /// stays linear in the source, which is why the parse carries no wall-clock timeout.
    /// </summary>
    [Test]
    public async Task Parse_Should_Read_The_First_And_Last_Route_Of_A_Client_Forty_Times_The_Pinned_Size()
    {
        const int copies = 3000;
        var client = string.Concat(Enumerable.Range(0, copies)
            .Select(static copy => Client.Replace("/api/vcs/diff", RoutePath(copy), StringComparison.Ordinal)));

        var contract = UpstreamClientContract.Parse(client, Api);

        _ = contract.TryGetQueryType("GET", RoutePath(0), "mode", out var first, out var firstProblem);
        _ = contract.TryGetQueryType("GET", RoutePath(copies - 1), "mode", out var last, out var lastProblem);
        await Assert.That(first).IsEqualTo("Vcs.Mode").Because(firstProblem);
        await Assert.That(last).IsEqualTo("Vcs.Mode").Because(lastProblem);

        static string RoutePath(int copy) => "/api/vcs/diff" + copy.ToString(CultureInfo.InvariantCulture);
    }

    [Test]
    [Arguments("GET", "/api/vcs/other", "mode", "has no route for GET /api/vcs/other")]
    [Arguments("GET", "/api/vcs/diff", "missing", "carries no field for the query key 'missing'")]
    public async Task TryGetQueryType_Should_Say_Why_There_Is_No_Type(string method, string path, string key, string expected)
    {
        var contract = UpstreamClientContract.Parse(Client, Api);

        var found = contract.TryGetQueryType(method, path, key, out _, out var problem);

        await Assert.That(found).IsFalse();
        await Assert.That(problem).Contains(expected);
    }

    [Test]
    [Arguments("number", true)]
    [Arguments("number | null", true)]
    [Arguments("string", false)]
    [Arguments("boolean", false)]
    [Arguments("\"asc\" | \"desc\"", false)]
    [Arguments("\"a \\\" b\" | null", false)]
    [Arguments("Event.Seq", true)]
    [Arguments("Session.ID | null", false)]
    [Arguments("{ readonly after?: number; readonly follow?: boolean }[\"after\"]", true)]
    [Arguments("{ readonly after?: number; readonly follow?: boolean }[\"follow\"]", false)]
    public async Task IsNumeric_Should_Classify_The_Codegen_Grammar(string type, bool expected) =>
        await Assert.That(UpstreamClientContract.IsNumeric(type, Named)).IsEqualTo(expected);

    /// <summary>A mixed union, an unknown name, a missing indexed field or a construct outside the grammar is unknown, not a guess.</summary>
    [Test]
    [Arguments("\"all\" | number")]
    [Arguments("number | string")]
    [Arguments("Unknown.Brand")]
    [Arguments("{ readonly after?: number }[\"missing\"]")]
    [Arguments("ReadonlyArray<number>")]
    [Arguments("\"unterminated")]
    [Arguments("null")]
    public async Task IsNumeric_Should_Read_Anything_Else_As_Unknown(string type) =>
        await Assert.That(UpstreamClientContract.IsNumeric(type, Named)).IsNull();

    private static bool? Named(string name) => name switch
    {
        "Event.Seq" => true,
        "Session.ID" or "Vcs.Mode" => false,
        _ => null,
    };
}
