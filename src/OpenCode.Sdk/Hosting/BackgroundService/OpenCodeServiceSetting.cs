namespace OpenCode.Sdk;

/// <summary>A setting removed by <see cref="OpenCodeServiceConfiguration.UnsetAsync"/>.</summary>
public enum OpenCodeServiceSetting
{
    /// <summary>The saved disabled preference.</summary>
    Disabled = 0,
    /// <summary>The saved hostname.</summary>
    Hostname = 1,
    /// <summary>The saved listen port.</summary>
    Port = 2,
    /// <summary>The saved Basic password.</summary>
    Password = 3,
    /// <summary>The saved CORS origins.</summary>
    CorsOrigins = 4,
}
