using OpenCode.Playground.Hq.Features.Sessions;

namespace OpenCode.Playground.Hq.Tests.Features.Sessions;

public sealed class SessionsScreenTests
{
    [Test]
    [Arguments(@"C:\work\project", "project")]
    [Arguments("/work/project", "project")]
    [Arguments(@"C:\work\project\", "project")]
    [Arguments("/work/project/", "project")]
    public async Task FolderName_Should_Take_The_Last_Segment_Of_Either_Path_Style(string path, string expected) =>
        await Assert.That(SessionsScreen.FolderName(path)).IsEqualTo(expected);
}
