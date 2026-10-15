namespace OpenCode.Sdk.Tools.Clones;

/// <summary>Which side of the generator's manifest a clone scope leaves out of its scan.</summary>
internal enum CloneScopeFilter
{
    /// <summary>The scope scans every C# file under its path.</summary>
    None,

    /// <summary>The scope leaves out every file the generator's manifest lists: it scans hand-written code.</summary>
    Generated,

    /// <summary>The scope leaves out every C# file the manifest does not list: it scans generated code.</summary>
    HandWritten,
}
