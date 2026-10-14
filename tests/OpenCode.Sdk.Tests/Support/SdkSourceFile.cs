namespace OpenCode.Sdk.Tests.Support;

/// <summary>One SDK source file: its project-relative path and its lines.</summary>
internal sealed record SdkSourceFile(string RelativePath, IReadOnlyList<string> Lines)
{
    /// <summary>Gets the folders between the project root and the file.</summary>
    public IEnumerable<string> Folders => RelativePath.Split('/').Reverse().Skip(1).Reverse();

    /// <summary>Gets the type the file declares by name: its file name up to the first dot, so a partial part names its type.</summary>
    public string TypeName => RelativePath.Split('/')[^1].Split('.')[0];
}
