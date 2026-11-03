using System.IO.Abstractions;
using System.Xml.Linq;
using OpenCode.Sdk.Tools.TestRoster;

namespace OpenCode.Sdk.Tools.Tests.RepositoryGates;

internal sealed class HangDumpRosterGuard(IFileSystem fileSystem)
{
    public async Task<IReadOnlyList<string>> FindAsync(string root)
    {
        var roster = await new TestRosterLoader(fileSystem).LoadAsync(
            fileSystem.Path.Combine(root, "tests", "test-roster.json"), CancellationToken.None);
        var modules = roster.Passes.Values.SelectMany(static systems => systems.Values)
            .SelectMany(static entries => entries).Select(static entry => entry.Split('/')[0])
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var violations = new List<string>();
        foreach (var module in modules)
        {
            var path = fileSystem.Path.Combine(root, "tests", module, module + ".csproj");
            if (!fileSystem.File.Exists(path))
            {
                violations.Add($"{module}: missing project tests/{module}/{module}.csproj; CI passes --hangdump, so a module without Microsoft.Testing.Extensions.HangDump exits 5 before running.");
                continue;
            }
            var project = XDocument.Parse(await fileSystem.File.ReadAllTextAsync(path));
            if (!project.Descendants("PackageReference").Attributes("Include")
                    .Any(static reference => reference.Value == "Microsoft.Testing.Extensions.HangDump"))
            {
                violations.Add($"{module}: missing Microsoft.Testing.Extensions.HangDump; CI passes --hangdump, so this module exits 5 before running.");
            }
        }

        return violations;
    }
}
