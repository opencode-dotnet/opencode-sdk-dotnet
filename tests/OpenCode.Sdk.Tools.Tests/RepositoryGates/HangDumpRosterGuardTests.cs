using System.Text.Json;
using System.Xml.Linq;
using OpenCode.Sdk.Tools.Tests.RepositoryGates.Support;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.RepositoryGates;

public sealed class HangDumpRosterGuardTests
{
    private static readonly string[] LinuxModules = ["OnlyHere/net10.0", "OnlyHere/net11.0"];
    private static readonly string[] WindowsModules = ["OnlyHere/net10.0"];

    [Test]
    public async Task FindAsync_Should_Require_HangDump_For_The_Repository_Roster()
    {
        var fileSystem = new RealFileSystem();
        var violations = await new HangDumpRosterGuard(fileSystem).FindAsync(new RepositoryRoot(fileSystem).Locate());

        await Assert.That(violations).IsEmpty();
    }

    [Test]
    [Arguments("main", true)]
    [Arguments("main", false)]
    [Arguments("service-elections", true)]
    [Arguments("service-elections", false)]
    [Arguments("native-aot", true)]
    [Arguments("native-aot", false)]
    public async Task FindAsync_Should_Check_Every_Pass(string pass, bool extension)
    {
        var project = new XElement("Project", new XElement("ItemGroup", extension
            ? new XElement("PackageReference", new XAttribute("Include", "Microsoft.Testing.Extensions.HangDump"))
            : null));
        var scenario = new RepositoryGateScenario()
            .WithFile("tests/test-roster.json", Roster(pass))
            .WithFile("tests/OnlyHere/OnlyHere.csproj", project.ToString());
        await scenario.BuildAsync();

        var violations = await new HangDumpRosterGuard(scenario.FileSystem).FindAsync(scenario.Root);

        string[] expected = extension ? [] :
            ["OnlyHere: missing Microsoft.Testing.Extensions.HangDump; CI passes --hangdump, so this module exits 5 before running."];
        await Assert.That(violations).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FindAsync_Should_Require_The_Rostered_Project(bool present)
    {
        var scenario = new RepositoryGateScenario().WithFile("tests/test-roster.json", Roster("native-aot"));
        if (present)
        {
            var project = new XElement("Project", new XElement("PackageReference",
                new XAttribute("Include", "Microsoft.Testing.Extensions.HangDump")));
            scenario.WithFile("tests/OnlyHere/OnlyHere.csproj", project.ToString());
        }
        await scenario.BuildAsync();

        var violations = await new HangDumpRosterGuard(scenario.FileSystem).FindAsync(scenario.Root);

        string[] expected = present ? [] :
            ["OnlyHere: missing project tests/OnlyHere/OnlyHere.csproj; CI passes --hangdump, so a module without Microsoft.Testing.Extensions.HangDump exits 5 before running."];
        await Assert.That(violations).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    private static string Roster(string pass) => JsonSerializer.Serialize(new
    {
        // An empty pass first, so a guard reading only the first pass misses the tested one.
        passes = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["empty"] = new { linux = Array.Empty<string>() },
            [pass] = new { linux = LinuxModules, windows = WindowsModules },
        },
    });
}
