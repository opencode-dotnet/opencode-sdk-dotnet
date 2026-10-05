using System.Text;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>Canned wire bodies and envelope shapes for the contract tests.</summary>
internal static class WireBodyData
{
    public const string ConfigShells =
        "[{\"path\":\"/bin/zsh\",\"name\":\"zsh\",\"acceptable\":true},"
        + "{\"path\":\"/bin/sh\",\"name\":\"sh\",\"acceptable\":false}]";

    public const string FormPendingState = "{\"status\":\"pending\"}";

    public const string FormAnsweredState = "{\"status\":\"answered\",\"answer\":{\"q1\":\"blue\"}}";

    public const string FormCancelledState = "{\"status\":\"cancelled\",\"message\":\"stop\"}";

    public static string FormDetail(string state) =>
        "{\"id\":\"frm_1\",\"sessionID\":\"ses_100\",\"title\":\"Approve deploy\",\"fields\":[],\"state\":" + state + "}";

    public const string IntegrationNotFoundError =
        "{\"_tag\":\"IntegrationNotFoundError\",\"integrationID\":\"int_9\",\"message\":\"gone\"}";

    public const string InfoOk = "{\"urls\":[\"http://localhost:4096\"],\"version\":\"0.0.0-test\",\"pid\":42,\"paths\":{\"tmp\":\"/tmp/opencode\"}}";

    public const string InfoWithUnknownField =
        "{\"urls\":[\"http://localhost:4096\"],\"version\":\"0.0.0-test\",\"pid\":42,\"paths\":{\"tmp\":\"/tmp/opencode\"},\"unexpected\":true}";

    public const string InfoMissingRequiredMember = "{\"urls\":[\"http://localhost:4096\"],\"pid\":42,\"paths\":{\"tmp\":\"/tmp/opencode\"}}";

    public const string InfoMissingUrls = "{\"version\":\"0.0.0-test\",\"pid\":42,\"paths\":{\"tmp\":\"/tmp/opencode\"}}";

    public const string InfoWithWrongTokenType = "{\"urls\":[\"http://localhost:4096\"],\"version\":\"0.0.0-test\",\"pid\":\"forty-two\",\"paths\":{\"tmp\":\"/tmp/opencode\"}}";

    public static byte[] InfoWithMalformedUtf8UnknownField()
    {
        var prefix = Encoding.UTF8.GetBytes(
            "{\"urls\":[\"http://localhost:4096\"],\"version\":\"0.0.0-test\",\"pid\":42,\"paths\":{\"tmp\":\"/tmp/opencode\"},\"unexpected\":\"");
        var suffix = Encoding.UTF8.GetBytes("\"}");
        return [.. prefix, 0xFF, .. suffix];
    }

    public const string UnauthorizedError = "{\"_tag\":\"UnauthorizedError\",\"message\":\"password required\"}";

    public const string SessionNotFoundError = "{\"_tag\":\"SessionNotFoundError\",\"sessionID\":\"ses_9\",\"message\":\"gone\"}";

    public const string MessageNotFoundError =
        "{\"_tag\":\"MessageNotFoundError\",\"sessionID\":\"ses_9\",\"messageID\":\"msg_1\",\"message\":\"gone\"}";

    public const string UnknownError = "{\"_tag\":\"UnknownError\",\"message\":\"boom\"}";

    public const string InvalidCursorError = "{\"_tag\":\"InvalidCursorError\",\"message\":\"stale\"}";

    public const string InvalidRequestError = "{\"_tag\":\"InvalidRequestError\",\"message\":\"bad\"}";

    public const string StreamTestBodyOpen = "{\"value\":\"open\"}";

    public const string ShellNotFoundError = "{\"_tag\":\"ShellNotFoundError\",\"id\":\"sh_9\",\"message\":\"gone\"}";

    /// <summary>The worktree error: tagged under '_tag' like every pinned error, with its payload nested under 'data' beside a 'name'.</summary>
    public const string WorktreeError =
        "{\"_tag\":\"WorktreeError\",\"name\":\"WorktreeError\",\"data\":{\"message\":\"the worktree has uncommitted changes\",\"forceRequired\":true}}";

    public const string Worktree = "{\"directory\":\"/repo/feature\"}";

    public const string Worktrees = "[{\"directory\":\"/repo\",\"strategy\":\"branch\"},{\"directory\":\"/repo-2\"}]";

    public const string PermissionNotFoundError =
        "{\"_tag\":\"PermissionNotFoundError\",\"requestID\":\"req_9\",\"message\":\"gone\"}";

    public const string ConflictError = "{\"_tag\":\"ConflictError\",\"message\":\"already delivered\"}";

    public const string SessionInterrupted = "{\"interrupted\":true}";

    public const string McpServerNotFoundError = "{\"_tag\":\"McpServerNotFoundError\",\"server\":\"docs\",\"message\":\"gone\"}";

    public const string PtyNotFoundError = "{\"_tag\":\"PtyNotFoundError\",\"ptyID\":\"pty_9\",\"message\":\"gone\"}";

    public const string ForbiddenError = "{\"_tag\":\"ForbiddenError\",\"message\":\"not allowed\"}";

    public const string ServiceUnavailableError =
        "{\"_tag\":\"ServiceUnavailableError\",\"message\":\"opencode-pty could not be started\",\"service\":\"opencode-pty\"}";

    public const string PersistentPtyConnectTokenBody = "{\"ticket\":\"tkt_p1\",\"expires_in\":60}";

    public const string AgentNotFoundError = "{\"_tag\":\"AgentNotFoundError\",\"agentID\":\"missing\",\"message\":\"gone\"}";

    public const string ProviderNotFoundError =
        "{\"_tag\":\"ProviderNotFoundError\",\"providerID\":\"prov_9\",\"message\":\"gone\"}";

    public const string ProjectNotFoundError =
        "{\"_tag\":\"ProjectNotFoundError\",\"projectID\":\"prj_9\",\"message\":\"gone\"}";

    /// <summary>One filesystem entry, the element both the fs list and fs find payloads carry.</summary>
    public const string FileSystemEntry = "{\"path\":\"src/App.cs\",\"type\":\"file\"}";

    /// <summary>One page of captured shell output, the payload shell.output's location envelope carries.</summary>
    public const string ShellOutputPage =
        "{\"output\":\"build ok\\n\",\"cursor\":24,\"size\":96,\"truncated\":true}";

    /// <summary>The directory every location-scoped envelope in this corpus answers from.</summary>
    public const string ResolvedDirectory = "/repo";

    public const string ResolvedLocation =
        "{\"directory\":\"" + ResolvedDirectory + "\",\"project\":{\"id\":\"prj_1\",\"directory\":\"/repo\",\"canonical\":\"/repo\"}}";

    public static string Envelope(string datum)
    {
        ArgumentNullException.ThrowIfNull(datum);

        return $"{{\"data\":{datum}}}";
    }

    public static string LocationEnvelope(string datum, string location = ResolvedLocation)
    {
        ArgumentNullException.ThrowIfNull(datum);
        ArgumentNullException.ThrowIfNull(location);

        return $"{{\"location\":{location},\"data\":{datum}}}";
    }

    public static string Page(string items, string? previous = null, string? next = null)
    {
        ArgumentNullException.ThrowIfNull(items);

        var cursor = (previous, next) switch
        {
            (null, null) => "{}",
            (not null, null) => $"{{\"previous\":\"{previous}\"}}",
            (null, not null) => $"{{\"next\":\"{next}\"}}",
            _ => $"{{\"previous\":\"{previous}\",\"next\":\"{next}\"}}",
        };
        return $"{{\"data\":[{items}],\"cursor\":{cursor}}}";
    }

    /// <summary>One durable session event at envelope version 1, as the log writes it.</summary>
    public const string SessionCreatedEvent =
        "{\"id\":\"evt_1\",\"created\":1,\"type\":\"session.created\","
        + "\"durable\":{\"aggregateID\":\"ses_9\",\"seq\":1,\"version\":1},"
        + "\"location\":{\"directory\":\"/repo\"},"
        + "\"data\":{\"sessionID\":\"ses_9\",\"projectID\":\"prj_1\",\"location\":{\"directory\":\"/repo\"},"
        + "\"slug\":\"first\",\"title\":\"first\",\"agent\":\"build\",\"version\":\"1\"}}";

    /// <summary>A durable event at envelope version 2; the family carries both versions.</summary>
    public const string SessionDeletedEvent =
        "{\"id\":\"evt_2\",\"created\":2,\"type\":\"session.deleted\","
        + "\"durable\":{\"aggregateID\":\"ses_9\",\"seq\":2,\"version\":2},"
        + "\"location\":{\"directory\":\"/repo\"},"
        + "\"data\":{\"sessionID\":\"ses_9\"}}";

    /// <summary>The watermark the log emits once its replay reaches the captured sequence.</summary>
    public const string LogSyncedEvent =
        "{\"type\":\"log.synced\",\"aggregateID\":\"ses_9\",\"seq\":2}";

    /// <summary>A tag no generated variant owns; the carrier preserves it (ADR-0009).</summary>
    public const string UnknownLogEvent =
        "{\"id\":\"evt_3\",\"created\":3,\"type\":\"session.invented.tomorrow\","
        + "\"durable\":{\"aggregateID\":\"ses_9\",\"seq\":3,\"version\":1},\"data\":{}}";

    /// <summary>The cause a mid-stream failure frame carries under the declared failure event.</summary>
    public const string StreamFailureCause = "[{\"_tag\":\"Die\",\"defect\":\"boom\"}]";

    /// <summary>A future cause tag preserved through ADR-0009's unknown carrier.</summary>
    public const string UnknownStreamFailureCause = "[{\"_tag\":\"FutureCause\",\"detail\":\"later\"}]";

    /// <summary>The declared but uninhabitable Fail branch, which must remain a protocol failure.</summary>
    public const string ImpossibleStreamFailureCause = "[{\"_tag\":\"Fail\",\"error\":\"boom\"}]";

    public const string StreamInterruptNullCause = "[{\"_tag\":\"Interrupt\",\"fiberId\":null}]";

    public const string StreamInterruptNumberCause = "[{\"_tag\":\"Interrupt\",\"fiberId\":42}]";

    public const string MultipleStreamFailureCauses =
        "[{\"_tag\":\"Die\",\"defect\":\"boom\"},{\"_tag\":\"Interrupt\",\"fiberId\":42}]";

    public const string EmptyStreamFailureCause = "[]";

    /// <summary>Frames one payload per event-stream frame, the shape the reader consumes.</summary>
    public static string Frames(params string[] payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);

        return string.Concat(payloads.Select(static payload => $"data: {payload}\n\n"));
    }

    /// <summary>Frames a payload under an explicit event name, which the wire writes only for a signal.</summary>
    public static string NamedFrame(string name, string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(payload);

        return $"event: {name}\ndata: {payload}\n\n";
    }
}
