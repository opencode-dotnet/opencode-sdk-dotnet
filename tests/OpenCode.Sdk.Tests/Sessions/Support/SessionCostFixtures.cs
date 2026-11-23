using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;

namespace OpenCode.Sdk.Tests.Sessions.Support;

internal sealed class SessionCostFixtures
{
    private readonly SessionInfo _template = JsonSerializer.Deserialize(
        new FixtureLoader().LoadJson("Serialization.known-session.json"), OpenCodeJsonContext.Default.SessionInfo)
        ?? throw new InvalidOperationException("The session template is null.");

    internal SessionInfo Session(string id, double cost, string? parentId = null) =>
        _template with { Id = id, Cost = cost, ParentId = parentId };

    internal static string Get(SessionInfo session) =>
        new JsonObject { ["data"] = JsonSerializer.SerializeToNode(session, OpenCodeJsonContext.Default.SessionInfo) }.ToJsonString();

    internal string Page(int start, int count, string? next = null) =>
        Page(Enumerable.Range(start, count).Select(index =>
            Session("ses_child_" + index.ToString(CultureInfo.InvariantCulture), 1, "ses_root")), next);

    internal static string Page(IEnumerable<SessionInfo> sessions, string? next = null)
    {
        var items = new JsonArray();
        foreach (var session in sessions)
        {
            items.Add(JsonSerializer.SerializeToNode(session, OpenCodeJsonContext.Default.SessionInfo));
        }

        return new JsonObject
        {
            ["data"] = items,
            ["cursor"] = new JsonObject { ["next"] = next },
        }.ToJsonString();
    }
}
