namespace OpenCode.Sdk.Internal;

/// <summary>
/// One decoded registration: what the daemon published so a client can reach it. Deliberately a
/// class, not a record, for the reason <c>ConnectionSnapshot</c> is one: a record synthesizes a
/// member-printing <see cref="object.ToString"/>, and this type carries the daemon's password.
/// Nothing here needs value equality; the identity comparison Stop needs is its own type.
/// </summary>
internal sealed class ServiceRegistration
{
    public ServiceRegistration(string? id, string? version, string url, Uri endpoint, int processId, string? password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(endpoint);

        Id = id;
        Version = version;
        Url = url;
        Endpoint = endpoint;
        ProcessId = processId;
        Password = password;
    }

    /// <summary>Gets the daemon's instance id, when it published one.</summary>
    public string? Id { get; }

    /// <summary>Gets the version the daemon published, when it published one; the info answer must repeat it.</summary>
    public string? Version { get; }

    /// <summary>Gets the raw URL string exactly as written; identity comparisons use this, never a normalized form.</summary>
    public string Url { get; }

    /// <summary>Gets the validated absolute HTTP or HTTPS endpoint the probe resolves against.</summary>
    public Uri Endpoint { get; }

    /// <summary>Gets the daemon's process id; the info answer must repeat it.</summary>
    public int ProcessId { get; }

    /// <summary>Gets the Basic password exactly as written, or null when the record carried none.</summary>
    public string? Password { get; }
}
