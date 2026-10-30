using System.Net.WebSockets;
using System.Text;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Ptys;

public sealed class PtyOutputFrameTests
{
    [Test]
    public async Task Text_Should_Return_The_Same_Instance_On_Repeated_Reads()
    {
        var frame = ReadOutput(Encoding.UTF8.GetBytes("repeated reads"));

        var first = frame.Text;
        var second = frame.Text;

        await Assert.That(second).IsSameReferenceAs(first);
    }

    [Test]
    public async Task Text_Should_Return_The_Published_Instance_On_Another_Thread()
    {
        var frame = ReadOutput(Encoding.UTF8.GetBytes("published across threads"));
        var first = frame.Text;
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(frame.Text);
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });

        thread.Start();
        thread.Join();

        await Assert.That(await completion.Task).IsSameReferenceAs(first);
    }

    [Test]
    public async Task Text_Should_Return_One_Instance_To_Racing_Readers()
    {
        // Sixteen fresh frames and four dedicated threads exercise repeated first-read contention
        // without a large thread budget. The maximum chunk's 65536 characters widen the window for
        // overlapping first reads. The barrier cannot guarantee overlapping decodes: this is
        // supplementary stress, not proof; the sequential identity rows are deterministic.
        const int frameCount = 16;
        const int threadCount = 4;
        var payload = Encoding.UTF8.GetBytes(new string('é', 65536));
        for (var iteration = 0; iteration < frameCount; iteration++)
        {
            var frame = ReadOutput(payload);
            using var barrier = new Barrier(threadCount);
            var results = new string?[threadCount];
            var failures = new Exception?[threadCount];
            var threads = Enumerable.Range(0, threadCount).Select(index => new Thread(() =>
            {
                try
                {
                    barrier.SignalAndWait();
                    results[index] = frame.Text;
                }
                catch (Exception exception)
                {
                    failures[index] = exception;
                }
            })).ToArray();

            foreach (var thread in threads)
            {
                thread.Start();
            }

            foreach (var thread in threads)
            {
                thread.Join();
            }

            for (var index = 0; index < threadCount; index++)
            {
                await Assert.That(failures[index]).IsNull();
                await Assert.That(results[index]).IsNotNull();
                await Assert.That(results[index]).IsSameReferenceAs(results[0]);
            }
        }
    }

    [Test]
    public async Task PtyOutputFrame_Should_Return_The_Exact_Text_Instance_Given_To_Its_Constructor()
    {
        var text = new string('x', 32);

        var frame = new PtyOutputFrame(text);

        await Assert.That(frame.Text).IsSameReferenceAs(text);
    }

    [Test]
    public async Task PtyOutputFrame_Should_Refuse_A_Null_Text()
    {
        // In this friend assembly, the internal span constructor makes a bare null ambiguous.
        _ = Assert.Throws<ArgumentNullException>(() => _ = new PtyOutputFrame(text: null!));

        await Task.CompletedTask;
    }

    private static PtyOutputFrame ReadOutput(byte[] payload) =>
        (PtyOutputFrame)PtyFrameReader.Read(WebSocketMessageType.Text, payload, payload.Length);
}
