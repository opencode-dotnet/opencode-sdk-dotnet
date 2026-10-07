using System.Diagnostics;
using System.Globalization;

namespace OpenCode.Sdk.ServiceFixture;

/// <summary>
/// The <c>tree-root</c> mode: the root of a two-process tree for the tree-kill proofs. It starts
/// one <c>idle</c> of this fixture as its child, waits for the child's <c>ready</c> line, prints
/// <c>ready child=&lt;pid&gt;</c> on stdout naming the child, and then sleeps until something ends
/// it. No member of the tree ends because another member ended: the root waits on nothing the
/// child does, and the child does not watch its parent. So when a tree kill reports that it ended
/// every member, each member was ended by the kill and not by its own exit.
/// </summary>
/// <remarks>
/// After the ready line the root touches no stream at all, and the child's three standard streams
/// are pipes to the root that neither side uses again. A console host that the kill ends first, or
/// a pipe whose other end is gone, therefore cannot make a member fail and exit on its own.
/// </remarks>
internal static class TreeRootMode
{
    private const string ChildReadyLine = "ready";

    public static async Task<int> RunAsync()
    {
        var info = new ProcessStartInfo(Environment.ProcessPath ?? "dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add(typeof(TreeRootMode).Assembly.Location);
        info.ArgumentList.Add("idle");

        // The root holds the child's handle for its whole life, so the child's pid cannot be
        // reused while the root runs, and the test can open the child by that pid.
        using var child = Process.Start(info) ?? throw new InvalidOperationException("The child did not start.");
        var line = await child.StandardOutput.ReadLineAsync().ConfigureAwait(false);
        if (!string.Equals(line, ChildReadyLine, StringComparison.Ordinal))
        {
            // The child is not the lingering process the test expects; end it so that no
            // half-started tree is left behind.
            child.Kill();
            await Console.Error.WriteLineAsync($"The child printed '{line}' instead of '{ChildReadyLine}'.").ConfigureAwait(false);
            return 1;
        }

        await Console.Out
            .WriteLineAsync($"ready child={child.Id.ToString(CultureInfo.InvariantCulture)}")
            .ConfigureAwait(false);
        await Console.Out.FlushAsync().ConfigureAwait(false);

        // A gate nobody opens: the root lingers until something ends it, which is the point.
        using var forever = new SemaphoreSlim(0);
        await forever.WaitAsync().ConfigureAwait(false);
        return 0;
    }
}
