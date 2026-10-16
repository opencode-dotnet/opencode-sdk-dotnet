using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Hosting.Platform.Posix;

/// <summary>
/// The per-OS signal-name tables: every standard signal has exactly one name in each, the numbers
/// Linux and macOS assign differently resolve to each system's own signal, and a number outside
/// the standard range has no name.
/// </summary>
public sealed class PosixSignalNamesTests
{
    public static IEnumerable<int> StandardSignals() => Enumerable.Range(1, 31);

    /// <summary>The host selects macOS's numbering only on macOS; Windows exposes the otherwise-unused Linux table.</summary>
    [Test]
    public async Task ForCurrentProcess_Should_Select_The_Hosts_Signal_Numbering()
    {
        if (OperatingSystem.IsMacOS())
        {
            await Assert.That(PosixSignalNames.ForCurrentProcess.NameOf(10)).IsEqualTo("SIGBUS");
        }
        else
        {
            await Assert.That(PosixSignalNames.ForCurrentProcess.NameOf(10)).IsEqualTo("SIGUSR1");
        }
    }

    [Test]
    [MethodDataSource(nameof(StandardSignals))]
    public async Task NameOf_Should_Name_Every_Standard_Signal_On_Both_Systems(int number)
    {
        await Assert.That(PosixSignalNames.Linux.NameOf(number)).StartsWith("SIG");
        await Assert.That(PosixSignalNames.MacOS.NameOf(number)).StartsWith("SIG");
    }

    [Test]
    public async Task NameOf_Should_Give_Each_Standard_Signal_A_Distinct_Name()
    {
        await Assert.That(StandardSignals().Select(PosixSignalNames.Linux.NameOf).Distinct(StringComparer.Ordinal).Count()).IsEqualTo(31);
        await Assert.That(StandardSignals().Select(PosixSignalNames.MacOS.NameOf).Distinct(StringComparer.Ordinal).Count()).IsEqualTo(31);
    }

    [Test]
    [Arguments(7, "SIGBUS", "SIGEMT")]
    [Arguments(10, "SIGUSR1", "SIGBUS")]
    [Arguments(12, "SIGUSR2", "SIGSYS")]
    [Arguments(17, "SIGCHLD", "SIGSTOP")]
    [Arguments(31, "SIGSYS", "SIGUSR2")]
    public async Task NameOf_Should_Follow_Each_Systems_Own_Numbering(int number, string linux, string macOS)
    {
        await Assert.That(PosixSignalNames.Linux.NameOf(number)).IsEqualTo(linux);
        await Assert.That(PosixSignalNames.MacOS.NameOf(number)).IsEqualTo(macOS);
    }

    [Test]
    [Arguments(1, "SIGHUP")]
    [Arguments(2, "SIGINT")]
    [Arguments(6, "SIGABRT")]
    [Arguments(9, "SIGKILL")]
    [Arguments(11, "SIGSEGV")]
    [Arguments(15, "SIGTERM")]
    public async Task NameOf_Should_Agree_Where_Both_Systems_Share_The_Number(int number, string name)
    {
        await Assert.That(PosixSignalNames.Linux.NameOf(number)).IsEqualTo(name);
        await Assert.That(PosixSignalNames.MacOS.NameOf(number)).IsEqualTo(name);
    }

    [Test]
    [Arguments(0)]
    [Arguments(32)]
    [Arguments(34)]
    [Arguments(64)]
    public async Task NameOf_Should_Name_No_Signal_Outside_The_Standard_Range(int number)
    {
        await Assert.That(PosixSignalNames.Linux.NameOf(number)).IsNull();
        await Assert.That(PosixSignalNames.MacOS.NameOf(number)).IsNull();
    }
}
