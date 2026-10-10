using System.Globalization;
using System.IO.Pipes;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// The host descriptor reader the launcher's pipe proofs rest on: it names both ends of a pipe this
/// process opens, at their own descriptors, as ends of one pipe, and holds them no longer once the
/// pipe is closed; and repeated snapshots add no pipe. Linux and macOS only; Windows has no
/// descriptor table to read. Keyless <c>[NotInParallel]</c>: the host's descriptor table is the
/// subject, so no other test may open pipes while one runs.
/// </summary>
[NotInParallel]
public sealed class HostDescriptorsTests
{
    /// <summary>
    /// The checks start from the pipe's own client descriptor, look for exactly one other descriptor
    /// holding its peer, and after the close look at those two descriptors only, rather than diffing
    /// the whole process, so a runtime pipe that comes and goes elsewhere in the process cannot change
    /// the verdict, and a reader that names nothing fails at once.
    /// </summary>
    [Test]
    public async Task PipesAsync_Should_Name_Both_Ends_Of_A_Pipe_The_Host_Opens_And_Neither_Once_Closed()
    {
        if (OperatingSystem.IsWindows())
        {
            BranchReport.Print("Windows — no descriptor table to read from the host");
            return;
        }

        int clientFd;
        OpenDescriptor? clientEnd;
        List<KeyValuePair<int, OpenDescriptor>> serverEnds;
        using (var pipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None))
        {
            clientFd = int.Parse(pipe.GetClientHandleAsString(), NumberStyles.Integer, CultureInfo.InvariantCulture);
            var whileOpen = await HostDescriptors.PipesAsync();
            clientEnd = whileOpen.GetValueOrDefault(clientFd);
            serverEnds = clientEnd is null
                ? []
                : [.. whileOpen.Where(entry => entry.Key != clientFd && entry.Value.SharesPipeWith(clientEnd))];

            // Taking the client handle as a string hands it to the caller, so disposing the pipe no
            // longer closes it; the client end is closed here, as a launcher closes its child's.
            pipe.DisposeLocalCopyOfClientHandle();
        }

        var afterClose = await HostDescriptors.PipesAsync();

        await Assert.That(clientEnd).IsNotNull();
        await Assert.That(serverEnds).Count().IsEqualTo(1);
        var ends = new[] { clientEnd!, serverEnds[0].Value };
        var stillHeld = new[] { clientFd, serverEnds[0].Key }
            .Where(descriptor => afterClose.TryGetValue(descriptor, out var held) && ends.Any(end => held.SharesPipeWith(end)));
        await Assert.That(stillHeld).IsEmpty();
        BranchReport.Print("POSIX — both ends named at their own descriptors, neither held once closed");
    }

    /// <summary>
    /// A smoke check, not the guarantee: a reader that started a listing process could list that
    /// process's own start pipe in a snapshot, and fifty snapshots give such a reader many chances to
    /// show it. The guarantee is structural — the reader starts no process — and the test above is
    /// what proves the reader sees pipes at all, since an empty reader also adds nothing.
    /// </summary>
    [Test]
    public async Task PipesAsync_Should_Add_No_Pipe_Across_Repeated_Snapshots()
    {
        if (OperatingSystem.IsWindows())
        {
            BranchReport.Print("Windows — no descriptor table to read from the host");
            return;
        }

        var before = await HostDescriptors.PipesAsync();
        for (var snapshot = 0; snapshot < 50; snapshot++)
        {
            var after = await HostDescriptors.PipesAsync();
            await Assert.That(after.Where(entry => !before.Values.Any(known => known.SharesPipeWith(entry.Value)))).IsEmpty();
        }

        BranchReport.Print("POSIX — 50 snapshots added no pipe");
    }
}
