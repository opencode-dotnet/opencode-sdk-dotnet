namespace OpenCode.Sdk.Tools.Tests.Packaging;

/// <summary>One consumer publish: its exit code, its combined output, and its native executable.</summary>
/// <param name="ExitCode">The publish's exit code.</param>
/// <param name="Output">Standard output and standard error together.</param>
/// <param name="Executable">The path the published native executable lands at.</param>
public sealed record ConsumerPublish(int ExitCode, string Output, string Executable);
