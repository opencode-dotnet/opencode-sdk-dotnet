namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// Prints which platform arm a test took, as the <c>branch:</c> line the gate's logs carry, so a
/// passing run shows what each platform actually asserted.
/// </summary>
internal static class BranchReport
{
    /// <summary>Prints one branch line.</summary>
    /// <param name="line">The platform and what its arm asserted.</param>
    public static void Print(string line) => Console.WriteLine("branch: " + line);
}
