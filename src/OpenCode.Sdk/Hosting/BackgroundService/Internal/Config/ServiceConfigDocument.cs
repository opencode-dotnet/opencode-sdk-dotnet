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

    /// <summary>Gets present members in the decode's schema order, or the subsequent merge's order.</summary>
    public IReadOnlyList<string> MemberNames => Members;
    private List<string> Members => _members ??= [.. SchemaOrder.Where(IsPresent)];

    /// <summary>Assigns without moving an existing password, adding an absent one last.</summary>
    /// <param name="password">The credential to persist.</param>
    public void AssignPassword(string password)
    {
        var members = Members;
        if (Password is null)
        {
            members.Add("password");
        }

        Password = password;
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
    public bool? Disabled { get; init; }

    /// <summary>Gets the current remote route, retained only inside the SDK.</summary>
    public string? RemoteRoute { get; set; }

    /// <summary>Gets the earlier boolean remote setting, or null for a current-shape document.</summary>
    public bool? LegacyRemote { get; set; }

    /// <summary>Gets the acceptance marker, true when present and null otherwise.</summary>
    public bool? RemoteAccepted { get; init; }

    /// <summary>Gets the saved hostname, or null when unset.</summary>
    public string? Hostname { get; init; }

    /// <summary>Gets the saved port, or null when unset.</summary>
    public int? Port { get; init; }

    /// <summary>Gets the stored password, retained only inside the SDK.</summary>
    public string? Password { get; set; }

    /// <summary>Gets the saved origins in stored order, or null when unset.</summary>
    public IReadOnlyList<string>? CorsOrigins { get; init; }

    /// <summary>Gets the saved environment entries, or null when unset.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}
