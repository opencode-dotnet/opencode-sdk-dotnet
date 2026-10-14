namespace OpenCode.Sdk.Tools.Tests.Packaging;

/// <summary>One consumer build: its exit code and its combined output.</summary>
/// <param name="ExitCode">The build's exit code.</param>
/// <param name="Output">Standard output and standard error together.</param>
public sealed record ConsumerBuild(int ExitCode, string Output);
