namespace OpenCode.Sdk;

/// <summary>The way a resolved connection reaches and owns its server.</summary>
public enum OpenCodeServerConnectionMode
{
    /// <summary>An explicitly addressed server, whose lifetime belongs elsewhere.</summary>
    Explicit = 0,

    /// <summary>A private server whose lifetime belongs to the connection.</summary>
    Standalone = 1,

    /// <summary>A registered background service shared with other clients.</summary>
    Service = 2,
}
