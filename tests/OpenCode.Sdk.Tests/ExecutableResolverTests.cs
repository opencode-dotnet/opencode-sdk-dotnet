using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// The resolution policy is exercised entirely through the injected existence predicate, so both
/// platform dialects are provable on whichever host runs the suite and no test mutates the real
/// PATH, PATHEXT, or working directory.
/// </summary>
public sealed class ExecutableResolverTests
{
    private const string Command = "opencode";
    private const string FirstDirectory = @"C:\first";
    private const string SecondDirectory = @"C:\second";
    private const string UnixFirstDirectory = "/usr/local/bin";
    private const string UnixSecondDirectory = "/usr/bin";
    private const string WorkingDirectory = @"C:\work";

    [Test]
    public async Task Resolve_Should_Use_A_Rooted_Command_As_Given()
    {
        var resolver = new ExecutableResolver(Windows(FirstDirectory, extensions: null));

        var resolved = resolver.Resolve(@"C:\tools\opencode.exe");

        await Assert.That(resolved.Path).IsEqualTo(@"C:\tools\opencode.exe");
        await Assert.That(resolved.Command).IsEqualTo(@"C:\tools\opencode.exe");
    }

    [Test]
    public async Task Resolve_Should_Use_A_Command_With_A_Directory_Separator_As_Given_On_Windows()
    {
        var resolver = new ExecutableResolver(Windows(FirstDirectory, extensions: null));

        var resolved = resolver.Resolve(@".\bin\opencode.exe");

        await Assert.That(resolved.Path).IsEqualTo(@".\bin\opencode.exe");
    }

    /// <summary>
    /// On Unix a relative path is made absolute against this process's directory, once: the child
    /// can start in another working directory, where the same relative path would name another
    /// file, and the directory trampoline needs an absolute command.
    /// </summary>
    [Test]
    public async Task Resolve_Should_Anchor_A_Relative_Command_Path_At_The_Current_Directory_On_Unix()
    {
        var resolver = new ExecutableResolver(Unix(UnixFirstDirectory));

        var resolved = resolver.Resolve("./bin/opencode");

        await Assert.That(resolved.Path).IsEqualTo("/work/./bin/opencode");
        await Assert.That(resolved.Command).IsEqualTo("./bin/opencode");
    }

    [Test]
    public async Task Resolve_Should_Use_An_Absolute_Command_Path_As_Given_On_Unix()
    {
        var resolver = new ExecutableResolver(Unix(UnixFirstDirectory));

        var resolved = resolver.Resolve("/opt/opencode/bin/opencode");

        await Assert.That(resolved.Path).IsEqualTo("/opt/opencode/bin/opencode");
    }

    [Test]
    public async Task Resolve_Should_Take_The_First_PATH_Directory_That_Holds_The_Command()
    {
        var resolver = new ExecutableResolver(Windows(
            FirstDirectory + ";" + SecondDirectory,
            extensions: ".EXE",
            FirstDirectory + @"\opencode.EXE",
            SecondDirectory + @"\opencode.EXE"));

        var resolved = resolver.Resolve(Command);

        await Assert.That(resolved.Path).IsEqualTo(FirstDirectory + @"\opencode.EXE");
    }

    [Test]
    public async Task Resolve_Should_Try_PATHEXT_Extensions_In_Order_On_Windows()
    {
        // Both live in the same directory: only the PATHEXT order decides, and .CMD precedes
        // .EXE here to prove the list is honored rather than a built-in preference.
        var resolver = new ExecutableResolver(Windows(
            FirstDirectory,
            ".CMD;.EXE",
            FirstDirectory + @"\opencode.EXE",
            FirstDirectory + @"\opencode.CMD"));

        var resolved = resolver.Resolve(Command);

        await Assert.That(resolved.Path).IsEqualTo(FirstDirectory + @"\opencode.CMD");
    }

    [Test]
    public async Task Resolve_Should_Try_The_Conventional_Extensions_When_PATHEXT_Is_Absent()
    {
        var resolver = new ExecutableResolver(Windows(
            FirstDirectory, extensions: null, FirstDirectory + @"\opencode.BAT"));

        var resolved = resolver.Resolve(Command);

        await Assert.That(resolved.Path).IsEqualTo(FirstDirectory + @"\opencode.BAT");
    }

    [Test]
    public async Task Resolve_Should_Try_A_Name_That_Already_Carries_An_Extension_As_Given()
    {
        // The .EXE sibling is present and would win under PATHEXT; naming the shim explicitly
        // has to beat it, because the caller already made the choice.
        var resolver = new ExecutableResolver(Windows(
            FirstDirectory,
            ".EXE;.CMD",
            FirstDirectory + @"\opencode.EXE",
            FirstDirectory + @"\opencode.cmd"));

        var resolved = resolver.Resolve("opencode.cmd");

        await Assert.That(resolved.Path).IsEqualTo(FirstDirectory + @"\opencode.cmd");
    }

    [Test]
    public async Task Resolve_Should_Skip_Empty_PATH_Entries()
    {
        var resolver = new ExecutableResolver(Windows(
            ";;" + FirstDirectory + ";", ".EXE", FirstDirectory + @"\opencode.EXE"));

        var resolved = resolver.Resolve(Command);

        await Assert.That(resolved.Path).IsEqualTo(FirstDirectory + @"\opencode.EXE");
    }

    [Test]
    public async Task Resolve_Should_Resolve_A_Relative_PATH_Entry_Against_The_Current_Directory()
    {
        var resolver = new ExecutableResolver(Windows(
            "tools", ".EXE", WorkingDirectory + @"\tools\opencode.EXE"));

        var resolved = resolver.Resolve(Command);

        await Assert.That(resolved.Path).IsEqualTo(WorkingDirectory + @"\tools\opencode.EXE");
    }

    [Test]
    public async Task Resolve_Should_Search_A_Bare_Name_Without_Extensions_On_Unix()
    {
        var resolver = new ExecutableResolver(Unix(
            UnixFirstDirectory + ":" + UnixSecondDirectory, UnixSecondDirectory + "/opencode"));

        var resolved = resolver.Resolve(Command);

        await Assert.That(resolved.Path).IsEqualTo(UnixSecondDirectory + "/opencode");
    }

    [Test]
    public async Task Resolve_Should_Mark_A_Resolved_Batch_Shim_As_A_Batch_Script()
    {
        var resolver = new ExecutableResolver(Windows(
            FirstDirectory, ".CMD", FirstDirectory + @"\opencode.CMD"));

        var resolved = resolver.Resolve(Command);

        await Assert.That(resolved.IsBatchScript).IsTrue();
    }

    [Test]
    public async Task Resolve_Should_Not_Mark_A_Resolved_Executable_As_A_Batch_Script()
    {
        var resolver = new ExecutableResolver(Windows(
            FirstDirectory, ".EXE", FirstDirectory + @"\opencode.EXE"));

        var resolved = resolver.Resolve(Command);

        await Assert.That(resolved.IsBatchScript).IsFalse();
    }

    [Test]
    public async Task Resolve_Should_Not_Mark_A_Cmd_Suffixed_Name_As_A_Batch_Script_On_Unix()
    {
        // cmd.exe is a Windows fact: a Unix file that happens to end in .cmd is spawned directly.
        var resolver = new ExecutableResolver(Unix(
            UnixFirstDirectory, UnixFirstDirectory + "/opencode.cmd"));

        var resolved = resolver.Resolve("opencode.cmd");

        await Assert.That(resolved.IsBatchScript).IsFalse();
    }

    [Test]
    public async Task Resolve_Should_Report_The_Searched_Directories_And_Extensions_When_Windows_Resolution_Fails()
    {
        var resolver = new ExecutableResolver(Windows(
            FirstDirectory + ";" + SecondDirectory, ".COM;.EXE"));

        var failure = await Assert.That(() => resolver.Resolve(Command)).Throws<OpenCodeServerException>();

        await Assert.That(failure!.Message).Contains("'opencode'");
        await Assert.That(failure.Message).Contains("was not found on PATH");
        await Assert.That(failure.Message).Contains("2 directories");
        await Assert.That(failure.Message).Contains(".COM, .EXE");
        await Assert.That(failure.Message).Contains("OpenCodeServerOptions.Command");
        await Assert.That(failure.Message).Contains("@opencode/cli");
    }

    [Test]
    public async Task Resolve_Should_Report_The_Searched_Directories_When_Unix_Resolution_Fails()
    {
        var resolver = new ExecutableResolver(Unix(UnixFirstDirectory));

        var failure = await Assert.That(() => resolver.Resolve(Command)).Throws<OpenCodeServerException>();

        await Assert.That(failure!.Message).Contains("1 directory");
        await Assert.That(failure.Message).DoesNotContain("extension");
    }

    [Test]
    public async Task Resolve_Should_Report_An_Empty_Search_When_PATH_Is_Absent()
    {
        var resolver = new ExecutableResolver(Windows(searchPath: null, extensions: ".EXE"));

        var failure = await Assert.That(() => resolver.Resolve(Command)).Throws<OpenCodeServerException>();

        await Assert.That(failure!.Message).Contains("0 directories");
    }

    private static ExecutableSearchEnvironment Windows(
        string? searchPath, string? extensions, params string[] present) =>
        new()
        {
            IsWindows = true,
            SearchPath = searchPath,
            SearchExtensions = extensions,
            CurrentDirectory = WorkingDirectory,
            FileExists = candidate => present.Contains(candidate, StringComparer.OrdinalIgnoreCase),
        };

    private static ExecutableSearchEnvironment Unix(string? searchPath, params string[] present) =>
        new()
        {
            IsWindows = false,
            SearchPath = searchPath,
            SearchExtensions = null,
            CurrentDirectory = "/work",
            FileExists = candidate => present.Contains(candidate, StringComparer.Ordinal),
        };
}
