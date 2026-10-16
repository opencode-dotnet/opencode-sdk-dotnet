using System.IO.Abstractions;

namespace OpenCode.Sdk.Tests.Harness;

/// <summary>The repository-owned local plugin used by the pinned server's RPC live tests.</summary>
internal sealed class TestRpcPlugin
{
    internal const string Id = "opencode.sdk.test.rpc";

    internal TestRpcPlugin(IFileSystem fileSystem, string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        Directory = fileSystem.Path.Combine(repositoryRoot, "tests", "OpenCode.Sdk.Tests", "Harness", "Plugins", "sdk-test-rpc");
        EntryPoint = fileSystem.Path.Combine(Directory, "index.js");
    }

    internal string Directory { get; }

    internal string EntryPoint { get; }
}
