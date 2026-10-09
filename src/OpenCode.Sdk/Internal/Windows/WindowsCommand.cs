namespace OpenCode.Sdk.Internal.Windows;

/// <summary>What <c>CreateProcessW</c> starts for one resolved command, and the whole line it hands it.</summary>
/// <param name="ApplicationPath">The executable that runs: the resolved target, or the system cmd.exe for a batch shim; null for a bare name, which <c>CreateProcessW</c> then finds itself.</param>
/// <param name="CommandLine">The whole command line, <c>argv[0]</c> included.</param>
internal sealed record WindowsCommand(string? ApplicationPath, string CommandLine);
