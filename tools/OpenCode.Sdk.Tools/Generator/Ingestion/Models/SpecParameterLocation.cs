namespace OpenCode.Sdk.Tools.Generator.Ingestion.Models;

/// <summary>Identifies an admitted wire location for an operation parameter.</summary>
internal enum SpecParameterLocation
{
    /// <summary>The parameter is embedded in the path template.</summary>
    Path = 0,

    /// <summary>The parameter is carried in the query string.</summary>
    Query = 1,

    /// <summary>The parameter is carried as a request header.</summary>
    Header = 2,
}
