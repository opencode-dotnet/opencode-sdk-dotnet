using System.Reflection;
using System.Xml.Linq;
using OpenCode.Sdk.Tools.Output;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions;
using Testably.Abstractions.Testing;

namespace OpenCode.Sdk.Tools.Tests.Output;

/// <summary>
/// Checks response-file ownership and the formatter's process contract without starting a child.
/// </summary>
public sealed class CliWrapProjectFormatterTests
{
    [Test]
    public async Task FormatAsync_Should_Use_The_Repository_Target_Framework_Stamp()
    {
        var fs = new RealFileSystem();
        var root = new RepositoryRoot(fs).Locate();
        var props = XDocument.Parse(await fs.File.ReadAllTextAsync(fs.Path.Combine(root, "Directory.Build.props")));
        var expected = props.Descendants("DefaultTargetFramework").Single().Value;
        var stamp = typeof(ToolApp).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(static attribute => attribute.Key == "DefaultTargetFramework");

        await Assert.That(expected).IsNotEmpty();
        await Assert.That(stamp?.Value).IsEqualTo(expected);
    }

    [Test]
    [Arguments("dotnet-format.dll")]
    [Arguments("dotnet-format.deps.json")]
    [Arguments("dotnet-format.runtimeconfig.json")]
    public async Task FormatAsync_Should_Refuse_Missing_Bundled_Formatter_Files(string fileName)
    {
        var fs = CreateFileSystem();
        var runner = Discovery(fs);
        var path = fs.Path.GetFullPath($"sdk/10.0.303/DotnetTools/dotnet-format/{fileName}");
        fs.File.Delete(path);

        var exception = await Assert.That(() => new CliWrapProjectFormatter(fs, runner)
            .FormatAsync("Sdk.csproj", ["A.cs"], CancellationToken.None)).Throws<OutputRefusalException>();

        await Assert.That(exception!.Message)
            .IsEqualTo($"SDK 10.0.303 has no '{path}'; the generator formats through the SDK's bundled dotnet-format.");
        await Assert.That(fs.Directory.EnumerateFiles(fs.Path.GetTempPath(), "*.rsp")).IsEmpty();
        await Assert.That(runner.Invocations).Count().IsEqualTo(2);
    }

    [Test]
    public async Task FormatAsync_Should_Refuse_An_Empty_Target_Framework()
    {
        var fs = CreateFileSystem();
        var runner = new ScriptedProcessRunner();
        var refusal = await Assert.That(() => new CliWrapProjectFormatter(fs, runner, "")
            .FormatAsync("Sdk.csproj", ["A.cs"], CancellationToken.None)).Throws<OutputRefusalException>();

        await Assert.That(refusal!.Message).Contains("DefaultTargetFramework");
        await Assert.That(runner.Invocations).IsEmpty();
    }

    [Test]
    [Arguments("success")]
    [Arguments("failure")]
    [Arguments("cancel")]
    public async Task FormatAsync_Should_Use_The_Process_Contract_And_Clean_Up(string outcome)
    {
        var fs = CreateFileSystem();
        var runner = Discovery(fs);
        using var cancellation = new CancellationTokenSource();
        var directory = fs.Path.GetFullPath("sdk/10.0.303/DotnetTools/dotnet-format");
        string? content = null;
        runner.Expect("dotnet", () =>
        {
            var rsp = fs.Directory.EnumerateFiles(fs.Path.GetTempPath(), "*.rsp").Single();
            return ["exec", "--depsfile", fs.Path.Combine(directory, "dotnet-format.deps.json"),
                "--runtimeconfig", fs.Path.Combine(directory, "dotnet-format.runtimeconfig.json"),
                fs.Path.Combine(directory, "dotnet-format.dll"), "Sdk.csproj", "--no-restore", "@" + rsp];
        }, fs.Directory.GetCurrentDirectory(), outcome == "failure" ? ScriptedProcessRunner.Fail("format [diagnostic]") : null,
            sideEffect: async () =>
            {
                var rsp = fs.Directory.EnumerateFiles(fs.Path.GetTempPath(), "*.rsp").Single();
                content = await fs.File.ReadAllTextAsync(rsp);
                if (outcome == "cancel")
                {
                    await cancellation.CancelAsync();
                    runner.CancellationTokens[^1].ThrowIfCancellationRequested();
                }
            }, environment: new Dictionary<string, string?>(StringComparer.Ordinal) { ["TargetFramework"] = "net8.0" });
        var formatter = new CliWrapProjectFormatter(fs, runner, "net8.0");
        Func<Task> run = () => formatter.FormatAsync("Sdk.csproj", ["Sessions/Session Client.cs", "A.cs"], cancellation.Token);
        if (outcome == "failure")
        {
            var refusal = await Assert.That(run).Throws<OutputRefusalException>();
            await Assert.That(refusal!.Message).Contains("exited with code 1: format [diagnostic]");
        }
        else if (outcome == "cancel")
        {
            await Assert.That(run).Throws<OperationCanceledException>();
        }
        else
        {
            await run();
        }
        await Assert.That(content).IsEqualTo("--include\n\"Sessions/Session Client.cs\"\n\"A.cs\"\n");
        await Assert.That(runner.CancellationTokens).Count().IsEqualTo(3);
        await Assert.That(runner.CancellationTokens[^1]).IsEqualTo(cancellation.Token);
        await Assert.That(fs.Directory.EnumerateFiles(fs.Path.GetTempPath(), "*.rsp")).IsEmpty();
    }

    [Test]
    public async Task FormatAsync_Should_Remove_A_Partially_Written_Response_File()
    {
        var fs = CreateFileSystem();
        var runner = Discovery(fs);
        var injected = false;
        using var fault = fs.Intercept.Creating(FileSystemTypes.File, change =>
        {
            injected = true;
            fs.Initialize().WithFile(change.Path).Which(f => f.HasStringContent("partial response"));
            throw new IOException("response write failed");
        }, predicate: change => !injected && change.Path.EndsWith(".rsp", StringComparison.Ordinal));

        var exception = await Assert.That(() => new CliWrapProjectFormatter(fs, runner)
            .FormatAsync("Sdk.csproj", ["Models/A.cs"], CancellationToken.None)).Throws<IOException>();

        await Assert.That(exception!.Message).IsEqualTo("response write failed");
        await Assert.That(injected).IsTrue();
        await Assert.That(fs.Directory.EnumerateFiles(fs.Path.GetTempPath(), "*.rsp")).IsEmpty();
        await Assert.That(runner.Invocations).Count().IsEqualTo(2);
    }

    private static MockFileSystem CreateFileSystem()
    {
        var fs = new MockFileSystem();
        fs.Directory.CreateDirectory(fs.Path.GetTempPath());
        var formatter = fs.Path.GetFullPath("sdk/10.0.303/DotnetTools/dotnet-format");
        fs.Directory.CreateDirectory(formatter);
        foreach (var name in (string[])["dotnet-format.dll", "dotnet-format.deps.json", "dotnet-format.runtimeconfig.json"])
        {
            fs.Initialize().WithFile(fs.Path.Combine(formatter, name));
        }
        return fs;
    }

    private static ScriptedProcessRunner Discovery(MockFileSystem fs) => new ScriptedProcessRunner()
        .Expect("dotnet", ["--version"], fs.Directory.GetCurrentDirectory(), ScriptedProcessRunner.Ok("10.0.303\n"))
        .Expect("dotnet", ["--list-sdks"], fs.Directory.GetCurrentDirectory(),
            ScriptedProcessRunner.Ok($"10.0.303 [{fs.Path.GetFullPath("sdk")}]\n"));

    [Test]
    public async Task ResponseFileContent_Should_Open_With_The_Include_Option()
    {
        var content = CliWrapProjectFormatter.ResponseFileContent(["Models/A.cs"]);

        await Assert.That(content).StartsWith("--include\n");
    }

    [Test]
    public async Task ResponseFileContent_Should_Quote_Each_Path_On_Its_Own_Line_In_Input_Order()
    {
        string[] paths = ["Models/B.cs", "Models/A.cs", "Sessions/Session Client.cs"];

        var content = CliWrapProjectFormatter.ResponseFileContent(paths);

        await Assert.That(content).IsEqualTo("--include\n\"Models/B.cs\"\n\"Models/A.cs\"\n\"Sessions/Session Client.cs\"\n");
    }

    [Test]
    public async Task ResponseFileContent_Should_Refuse_A_Path_With_A_Double_Quote()
    {
        await Assert.That(() => CliWrapProjectFormatter.ResponseFileContent(["Models/\"A\".cs"]))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task SdkRoot_Should_Return_The_Install_Root_Of_The_Selected_Version()
    {
        const string listing = "9.0.318 [C:\\Program Files\\dotnet\\sdk]\r\n10.0.303 [C:\\Program Files\\dotnet\\sdk]\r\n";

        var root = CliWrapProjectFormatter.SdkRoot(listing, "10.0.303");

        await Assert.That(root).IsEqualTo("C:\\Program Files\\dotnet\\sdk");
    }

    [Test]
    public async Task SdkRoot_Should_Not_Match_A_Version_That_Only_Shares_A_Prefix()
    {
        const string listing = "10.0.3030 [/opt/other/sdk]\n10.0.303 [/usr/share/dotnet/sdk]\n";

        var root = CliWrapProjectFormatter.SdkRoot(listing, "10.0.303");

        await Assert.That(root).IsEqualTo("/usr/share/dotnet/sdk");
    }

    [Test]
    public async Task SdkRoot_Should_Refuse_A_Listing_Without_The_Selected_Version()
    {
        await Assert.That(() => CliWrapProjectFormatter.SdkRoot("9.0.318 [/usr/share/dotnet/sdk]\n", "10.0.303"))
            .Throws<OutputRefusalException>();
    }
}
