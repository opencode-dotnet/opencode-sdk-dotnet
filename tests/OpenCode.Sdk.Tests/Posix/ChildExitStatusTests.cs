using OpenCode.Sdk.Internal.Posix;

namespace OpenCode.Sdk.Tests.Posix;

/// <summary>
/// The <c>waitpid</c> status decoder against every status word a reaped child can leave: each exit
/// code a byte can hold, each standard signal with and without the core-dump flag, and each stop.
/// The words are built the way Linux and macOS lay them out: an exit code in the second byte over
/// zero low bits, a signal number in the low seven bits, and a stop as the stopping signal in the
/// second byte over the low-bits marker.
/// </summary>
public sealed class ChildExitStatusTests
{
    /// <summary>The flag a status word carries above the signal number when the death dumped core.</summary>
    private const int CoreDumpFlag = 0x80;

    /// <summary>The low-bits value of a stopped child.</summary>
    private const int StopMarker = 0x7f;

    public static IEnumerable<int> ExitCodes() => Enumerable.Range(0, 256);

    public static IEnumerable<int> StandardSignals() => Enumerable.Range(1, 31);

    public static IEnumerable<(int Signal, bool CoreDumped)> SignalDeaths() =>
        StandardSignals().SelectMany(static signal => new[] { (signal, false), (signal, true) });

    [Test]
    [MethodDataSource(nameof(ExitCodes))]
    public async Task FromWaitStatus_Should_Read_The_Exit_Code_When_The_Child_Exited(int code)
    {
        var status = ChildExitStatus.FromWaitStatus(code << 8);

        await Assert.That(status.ExitCode).IsEqualTo(code);
        await Assert.That(status.Signal).IsNull();
    }

    [Test]
    [MethodDataSource(nameof(SignalDeaths))]
    public async Task FromWaitStatus_Should_Read_The_Signal_When_A_Signal_Ended_The_Child(int signal, bool coreDumped)
    {
        var status = ChildExitStatus.FromWaitStatus(coreDumped ? signal | CoreDumpFlag : signal);

        await Assert.That(status.Signal).IsEqualTo(signal);
        await Assert.That(status.ExitCode).IsNull();
    }

    [Test]
    [MethodDataSource(nameof(StandardSignals))]
    public async Task FromWaitStatus_Should_Read_Unknown_When_The_Child_Only_Stopped(int signal)
    {
        var status = ChildExitStatus.FromWaitStatus((signal << 8) | StopMarker);

        await Assert.That(status.ExitCode).IsNull();
        await Assert.That(status.Signal).IsNull();
    }

    [Test]
    public async Task Describe_Should_Name_The_Exit_Code_When_The_Child_Exited()
    {
        var status = ChildExitStatus.FromWaitStatus(7 << 8);

        await Assert.That(status.Describe()).IsEqualTo("exited with code 7");
    }

    /// <summary>A Windows status code such as an access violation reads as a negative 32-bit value, and its hexadecimal spelling rides along.</summary>
    [Test]
    public async Task Describe_Should_Add_The_Hexadecimal_Form_Of_A_Negative_Code()
    {
        var status = new ChildExitStatus { ExitCode = unchecked((int)0xC0000005) };

        await Assert.That(status.Describe()).IsEqualTo("exited with code -1073741819 (0xC0000005)");
    }

    [Test]
    public async Task Describe_Should_Name_The_Signal_When_A_Signal_Ended_The_Child()
    {
        var status = ChildExitStatus.FromWaitStatus(9);

        await Assert.That(status.Describe()).IsEqualTo("terminated on signal 9");
    }

    [Test]
    public async Task Describe_Should_Say_The_Status_Is_Unknown_When_It_Could_Not_Be_Read()
    {
        await Assert.That(ChildExitStatus.Unknown.Describe()).IsEqualTo("exited with an unknown status");
    }
}
