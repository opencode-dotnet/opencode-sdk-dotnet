using System.Globalization;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// Skill discovery from a workspace's own <c>.opencode</c> directory is project configuration,
/// which every shared fixture's server has turned off. So this test starts a server of its own with
/// discovery on and puts the workspace inside that server's run root, the way upstream's own config
/// tests keep discovery on and put the project in a temp directory.
/// </summary>
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class SkillsClientLiveTests
{
    private const string SkillDescription = "Exercises local skill discovery for SDK live verification.";
    private const string SkillId = "sdk-live-skill";
    private const string SkillName = "SDK live skill";
    private const string SkillContent = "Use this skill to verify local SDK skill discovery.";

    private static readonly RealFileSystem FileSystem = new();

    [Test]
    [Timeout(240_000)]
    public async Task ListSkillsAsync_Should_Report_The_Workspace_Skill(CancellationToken cancellationToken)
    {
        using var runRoot = new TestRunRoot(FileSystem);
        await using var server = await new PinnedServerLaunch(FileSystem).StartWithProjectConfigurationAsync(runRoot, cancellationToken);
        using var workspace = new TestWorkspace(FileSystem, runRoot.Path);
        var fixture = new FixtureLoader().LoadText("Skills.sdk-live-skill.md");
        var skillPath = workspace.WriteTextFile(".opencode/skills/sdk-live-skill/SKILL.md", fixture);
        using var client = server.CreateClient(options => options.Location = new LocationSelector { Directory = workspace.Path });
        var response = await LiveReadiness.WaitAsync(
            token => client.Skills.ListSkillsAsync(cancellationToken: token),
            result => result.Skills.Any(item => item.Id == SkillId),
            "seeded skills", cancellationToken);

        await Assert.That(response.Status).IsEqualTo(200);
        await Assert.That(response.IsError).IsFalse();
        await Assert.That(response.Location.Directory).IsEqualTo(workspace.Path);
        var skill = response.Skills.Single(item => item.Id == SkillId);
        await Assert.That(skill.Name).IsEqualTo(SkillName);
        await Assert.That(skill.Description).IsEqualTo(SkillDescription);
        await Assert.That(skill.Path).IsEqualTo(skillPath);
        await Assert.That(skill.Content).IsEqualTo(SkillContent);

        Console.WriteLine(
            "skills-live: status=" + Number(response.Status) + " id=" + skill.Id + " location=" + skill.Path);
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
