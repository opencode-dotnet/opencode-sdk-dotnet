using OpenCode.Sdk.Internal.Windows;

namespace OpenCode.Sdk.Tests.Windows;

/// <summary>
/// <see cref="WindowsEnvironmentBlock"/>: the <c>CreateProcessW</c> block layout, ordered the way
/// .NET's <c>Process</c> and libuv order it.
/// </summary>
public sealed class WindowsEnvironmentBlockTests
{
    [Test]
    public async Task Build_Should_Order_Entries_Case_Insensitively_By_Name()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["windir"] = @"C:\Windows",
            ["Path"] = @"C:\bin",
            ["ALLUSERSPROFILE"] = @"C:\ProgramData",
            ["_underscore"] = "u",
        };

        var block = WindowsEnvironmentBlock.Build(environment);

        // Upper-cased ordinal order, the rule Windows documents: '_' (0x5F) sorts after 'Z' (0x5A).
        await Assert.That(block).IsEqualTo(
            "ALLUSERSPROFILE=C:\\ProgramData\0Path=C:\\bin\0windir=C:\\Windows\0_underscore=u\0\0");
    }

    [Test]
    public async Task Build_Should_Keep_An_Empty_Value()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["KEEP"] = "1",
            ["EMPTY"] = string.Empty,
        };

        var block = WindowsEnvironmentBlock.Build(environment);

        await Assert.That(block).IsEqualTo("EMPTY=\0KEEP=1\0\0");
    }

    [Test]
    public async Task Build_Should_End_An_Empty_Environment_With_The_Block_Terminator()
    {
        // The interop layer appends the string's own terminator, so the single NUL here becomes
        // the double NUL an empty block needs.
        var block = WindowsEnvironmentBlock.Build(new Dictionary<string, string>(StringComparer.Ordinal));

        await Assert.That(block).IsEqualTo("\0");
    }
}
