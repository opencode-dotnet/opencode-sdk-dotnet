using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Hosting.Launcher;

/// <summary>
/// The public end of an owned server: each kind sets exactly its own members, the factories refuse
/// what no end can carry, the description reads as the launcher's diagnostics read, and the
/// launcher's decoded status translates with the host's signal names.
/// </summary>
public sealed class OpenCodeServerExitTests
{
    /// <summary>An access violation's NTSTATUS, as Windows reports it in a signed 32-bit code.</summary>
    private const int AccessViolation = unchecked((int)0xC0000005);

    private const int Terminate = 15;

    [Test]
    public async Task FromExitCode_Should_Set_Only_The_Code()
    {
        var exit = OpenCodeServerExit.FromExitCode(7);

        await Assert.That(exit.Kind).IsEqualTo(OpenCodeServerExitKind.Exited);
        await Assert.That(exit.ExitCode).IsEqualTo(7);
        await Assert.That(exit.Signal).IsNull();
        await Assert.That(exit.SignalNumber).IsNull();
    }

    [Test]
    public async Task FromSignal_Should_Set_Only_The_Signal()
    {
        var exit = OpenCodeServerExit.FromSignal("SIGTERM", Terminate);

        await Assert.That(exit.Kind).IsEqualTo(OpenCodeServerExitKind.Signaled);
        await Assert.That(exit.Signal).IsEqualTo("SIGTERM");
        await Assert.That(exit.SignalNumber).IsEqualTo(Terminate);
        await Assert.That(exit.ExitCode).IsNull();
    }

    [Test]
    public async Task Unknown_Should_Set_No_Member()
    {
        var exit = OpenCodeServerExit.Unknown;

        await Assert.That(exit.Kind).IsEqualTo(OpenCodeServerExitKind.Unknown);
        await Assert.That(exit.ExitCode).IsNull();
        await Assert.That(exit.Signal).IsNull();
        await Assert.That(exit.SignalNumber).IsNull();
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task FromSignal_Should_Refuse_A_Signal_Number_That_Is_Not_Positive(int signalNumber) =>
        _ = await Assert.That(() => OpenCodeServerExit.FromSignal("SIGTERM", signalNumber)).Throws<ArgumentOutOfRangeException>();

    [Test]
    [Arguments("")]
    [Arguments(" ")]
    public async Task FromSignal_Should_Refuse_A_Blank_Name(string signal) =>
        _ = await Assert.That(() => OpenCodeServerExit.FromSignal(signal, Terminate)).Throws<ArgumentException>();

    [Test]
    public async Task Equals_Should_Compare_The_Ends_By_Value()
    {
        await Assert.That(OpenCodeServerExit.FromSignal("SIGTERM", Terminate)).IsEqualTo(OpenCodeServerExit.FromSignal("SIGTERM", Terminate));
        await Assert.That(OpenCodeServerExit.FromExitCode(1)).IsNotEqualTo(OpenCodeServerExit.FromExitCode(2));
    }

    [Test]
    public async Task ToString_Should_Describe_Each_End()
    {
        await Assert.That(OpenCodeServerExit.FromExitCode(1).ToString()).IsEqualTo("exited with code 1");
        await Assert.That(OpenCodeServerExit.FromExitCode(AccessViolation).ToString()).IsEqualTo("exited with code -1073741819 (0xC0000005)");
        await Assert.That(OpenCodeServerExit.FromSignal("SIGTERM", Terminate).ToString()).IsEqualTo("terminated on signal SIGTERM (15)");
        await Assert.That(OpenCodeServerExit.FromSignal(null, 40).ToString()).IsEqualTo("terminated on signal 40");
        await Assert.That(OpenCodeServerExit.Unknown.ToString()).IsEqualTo("exited with an unknown status");
    }

    [Test]
    public async Task From_Should_Name_The_Signal_By_The_Host_Table()
    {
        var bus = new ChildExitStatus { Signal = 10 };

        await Assert.That(OpenCodeServerExit.From(bus, PosixSignalNames.MacOS)).IsEqualTo(OpenCodeServerExit.FromSignal("SIGBUS", 10));
        await Assert.That(OpenCodeServerExit.From(bus, PosixSignalNames.Linux)).IsEqualTo(OpenCodeServerExit.FromSignal("SIGUSR1", 10));
    }

    [Test]
    public async Task From_Should_Keep_A_Signal_The_Table_Does_Not_Name_By_Its_Number()
    {
        var exit = OpenCodeServerExit.From(new ChildExitStatus { Signal = 40 }, PosixSignalNames.Linux);

        await Assert.That(exit).IsEqualTo(OpenCodeServerExit.FromSignal(null, 40));
    }

    [Test]
    public async Task From_Should_Translate_A_Code_And_An_Unknown_Status()
    {
        await Assert.That(OpenCodeServerExit.From(new ChildExitStatus { ExitCode = AccessViolation }, PosixSignalNames.Linux))
            .IsEqualTo(OpenCodeServerExit.FromExitCode(AccessViolation));
        await Assert.That(OpenCodeServerExit.From(ChildExitStatus.Unknown, PosixSignalNames.Linux)).IsEqualTo(OpenCodeServerExit.Unknown);
    }
}
