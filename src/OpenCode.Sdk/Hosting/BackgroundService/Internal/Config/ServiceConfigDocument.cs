namespace OpenCode.Sdk.Internal;

/// <summary>
/// The decoded service configuration, including the private password and remote route. A class,
/// not a record, so the default string representation never prints its members. Null means unset;
/// <see cref="LegacyRemote"/> also distinguishes the earlier boolean remote shape.
/// </summary>
internal sealed class ServiceConfigDocument
{
    private static readonly string[] SchemaOrder = ["disabled", "remote", "remoteAccepted", "hostname", "port", "password", "cors", "env"];
    private List<string>? _members;

    /// <summary>Initializes private settings before member order is first materialized in schema order.</summary>
    /// <param name="remoteRoute">The current remote route, or null when unset.</param>
    /// <param name="legacyRemote">The earlier boolean remote setting, or null for the current shape.</param>
    /// <param name="password">The stored password, or null when unset.</param>
    /// <param name="disabled">The saved disabled flag.</param>
    /// <param name="remoteAccepted">The saved acceptance marker.</param>
    /// <param name="hostname">The saved hostname.</param>
    /// <param name="port">The saved port.</param>
    /// <param name="corsOrigins">The saved origins.</param>
    /// <param name="environment">The saved environment entries.</param>
    internal ServiceConfigDocument(
        bool? disabled = null, string? remoteRoute = null, bool? legacyRemote = null, bool? remoteAccepted = null,
        string? hostname = null, int? port = null, string? password = null,
        IReadOnlyList<string>? corsOrigins = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        Disabled = disabled;
        RemoteRoute = remoteRoute;
        LegacyRemote = legacyRemote;
        RemoteAccepted = remoteAccepted;
        Hostname = hostname;
        Port = port;
        Password = password;
        CorsOrigins = corsOrigins;
        Environment = environment;
    }

    /// <summary>Gets present members in the decode's schema order, or the subsequent merge's order.</summary>
    public IReadOnlyList<string> MemberNames => Members;
    private List<string> Members => _members ??= [.. SchemaOrder.Where(IsPresent)];

    /// <summary>Assigns without moving an existing password, adding an absent one last.</summary>
    /// <param name="password">The credential to persist.</param>
    public void AssignPassword(string? password)
    {
        SetPresence("password", password is not null);
        Password = password;
    }

    /// <summary>Assigns the hostname without moving an existing member.</summary>
    /// <param name="hostname">The value, including an empty string.</param>
    public void AssignHostname(string? hostname)
    {
        SetPresence("hostname", hostname is not null);
        Hostname = hostname;
    }

    /// <summary>Assigns or drops the disabled preference without moving it.</summary>
    public void AssignDisabled(bool? disabled)
    {
        SetPresence("disabled", disabled is not null);
        Disabled = disabled;
    }

    /// <summary>Assigns acceptance without moving an existing marker.</summary>
    public void AssignRemoteAccepted(bool accepted)
    {
        SetPresence("remoteAccepted", accepted);
        RemoteAccepted = accepted ? true : null;
    }

    /// <summary>Assigns or drops the port without moving it.</summary>
    public void AssignPort(int? port)
    {
        SetPresence("port", port is not null);
        Port = port;
    }

    /// <summary>Assigns or drops origins without moving an existing member.</summary>
    public void AssignCorsOrigins(IReadOnlyList<string>? origins)
    {
        SetPresence("cors", origins is not null);
        CorsOrigins = origins;
    }

    /// <summary>Assigns or drops environment entries without moving an existing member.</summary>
    public void AssignEnvironment(IReadOnlyDictionary<string, string>? environment)
    {
        SetPresence("env", environment is not null);
        Environment = environment;
    }

    /// <summary>Removes a setting and its value; remote and environment have dedicated operations.</summary>
    public void RemoveSetting(OpenCodeServiceSetting setting)
    {
        switch (setting)
        {
            case OpenCodeServiceSetting.Disabled:
                AssignDisabled(null);
                break;
            case OpenCodeServiceSetting.Hostname:
                AssignHostname(null);
                break;
            case OpenCodeServiceSetting.Port:
                AssignPort(null);
                break;
            case OpenCodeServiceSetting.Password:
                AssignPassword(null);
                break;
            case OpenCodeServiceSetting.CorsOrigins:
                AssignCorsOrigins(null);
                break;
        }
    }

    /// <summary>Removes env before an unset appends any remaining entries last.</summary>
    public void RemoveEnvironment() => AssignEnvironment(null);

    private void SetPresence(string member, bool present)
    {
        var members = Members;
        if (!present)
        {
            _ = members.Remove(member);
        }
        else if (!members.Contains(member, StringComparer.Ordinal))
        {
            members.Add(member);
        }
    }

    /// <summary>Drops a legacy boolean, adding a generated remote object last only when enabled.</summary>
    /// <param name="route">The generated route, or null to drop remote.</param>
    public void RepairRemote(string? route)
    {
        var members = Members;
        _ = members.Remove("remote");
        LegacyRemote = null;
        RemoteRoute = route;
        if (route is not null)
        {
            members.Add("remote");
        }
    }

    private bool IsPresent(string member) => member switch
    {
        "disabled" => Disabled is not null,
        "remote" => RemoteRoute is not null || LegacyRemote is not null,
        "remoteAccepted" => RemoteAccepted is not null,
        "hostname" => Hostname is not null,
        "port" => Port is not null,
        "password" => Password is not null,
        "cors" => CorsOrigins is not null,
        "env" => Environment is not null,
        _ => false,
    };

    /// <summary>Gets the saved connection-mode preference, or null when unset.</summary>
    public bool? Disabled { get; private set; }

    /// <summary>Gets the current remote route, retained only inside the SDK.</summary>
    public string? RemoteRoute { get; private set; }

    /// <summary>Gets the earlier boolean remote setting, or null for a current-shape document.</summary>
    public bool? LegacyRemote { get; private set; }

    /// <summary>Gets the acceptance marker, true when present and null otherwise.</summary>
    public bool? RemoteAccepted { get; private set; }

    /// <summary>Gets the saved hostname, or null when unset.</summary>
    public string? Hostname { get; private set; }

    /// <summary>Gets the saved port, or null when unset.</summary>
    public int? Port { get; private set; }

    /// <summary>Gets the stored password, retained only inside the SDK.</summary>
    public string? Password { get; private set; }

    /// <summary>Gets the saved origins in stored order, or null when unset.</summary>
    public IReadOnlyList<string>? CorsOrigins { get; private set; }

    /// <summary>Gets the saved environment entries, or null when unset.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; private set; }
}
