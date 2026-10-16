using System.Text;
using OpenCode.Sdk.Tools.Generator.Refresh;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tools.Tests.Generator.Refresh;

public sealed class CliWrapProcessRunnerTests
{
    [Test]
    public async Task RunAsync_Should_Forward_Environment_Overrides_To_The_Child()
    {
        var fileSystem = new RealFileSystem();
        var runner = new CliWrapProcessRunner();
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "opencode.envprobe",
            ["GIT_CONFIG_VALUE_0"] = "forwarded",
        };

        var result = await runner.RunAsync("git", ["config", "--get", "opencode.envprobe"],
            fileSystem.Directory.GetCurrentDirectory(), environment, CancellationToken.None);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(Encoding.UTF8.GetString(result.StandardOutput).TrimEnd('\r', '\n')).IsEqualTo("forwarded");
        await Assert.That(result.StandardError).IsEmpty();
    }
}
