namespace OpenCode.Sdk.Tests.Support;

/// <summary>The signal a <see cref="LauncherHost"/> starts with ignored, inherited the way a real host inherits it.</summary>
internal enum LauncherHostIgnores
{
    /// <summary>Every signal at its default disposition.</summary>
    Nothing,

    /// <summary><c>SIGINT</c> ignored, as under <c>nohup</c> or a shell's <c>trap '' INT</c>.</summary>
    Interrupt,

    /// <summary><c>SIGCHLD</c> ignored, so the kernel's handling of an ignored <c>SIGCHLD</c> applies to the host's children.</summary>
    ChildExit,
}
