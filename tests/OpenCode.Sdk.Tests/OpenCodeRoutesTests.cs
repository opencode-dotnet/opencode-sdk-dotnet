using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Tests;

public sealed class OpenCodeRoutesTests
{
    [Test]
    [Arguments(".")]
    [Arguments("..")]
    [Arguments(" ")]
    public async Task GetMessage_Should_Refuse_An_Unsafe_Session_Segment(string sessionId)
    {
        var exception = Assert.Throws<ArgumentException>(() => _ = OpenCodeRoutes.Sessions.GetMessage(sessionId, "m"));

        await Assert.That(exception.ParamName).IsEqualTo("sessionId");
    }

    [Test]
    public async Task GetMessage_Should_Refuse_Lone_Surrogates()
    {
        string[] sessionIds = ["\ud800", "\udc00"];
        foreach (var sessionId in sessionIds)
        {
            var exception = Assert.Throws<ArgumentException>(() => _ = OpenCodeRoutes.Sessions.GetMessage(sessionId, "m"));

            await Assert.That(exception.ParamName).IsEqualTo("sessionId");
        }
    }

    [Test]
    [Arguments(".")]
    [Arguments("..")]
    [Arguments(" ")]
    public async Task GetMessage_Should_Refuse_An_Unsafe_Message_Segment(string messageId)
    {
        var exception = Assert.Throws<ArgumentException>(() => _ = OpenCodeRoutes.Sessions.GetMessage("s", messageId));

        await Assert.That(exception.ParamName).IsEqualTo("messageId");
    }

    [Test]
    public async Task GetMessage_Should_Escape_Reserved_Characters()
    {
        var route = OpenCodeRoutes.Sessions.GetMessage("a b", "c/d");

        await Assert.That(route).IsEqualTo("/api/session/a%20b/message/c%2Fd");
    }

    [Test]
    public async Task ListSessions_Should_Refuse_An_Oversized_Query_Value()
    {
        var request = new SessionListRequest { Search = new string('a', 32_767) };

        var exception = Assert.Throws<ArgumentException>(() => _ = OpenCodeRoutes.Sessions.ListSessions(request));

        await Assert.That(exception.ParamName).IsEqualTo("search");
    }

    [Test]
    public async Task GetMessage_Should_Accept_A_Valid_Surrogate_Pair()
    {
        var route = OpenCodeRoutes.Sessions.GetMessage("emoji-\ud83d\ude00", "m");

        await Assert.That(route).IsEqualTo("/api/session/emoji-%F0%9F%98%80/message/m");
    }

    [Test]
    public async Task FindEntries_Should_Carry_The_Required_Query_Alone_When_Nothing_Else_Is_Set()
    {
        var route = OpenCodeRoutes.FileSystem.FindEntries(new FsFindRequest { Query = "todo" });

        await Assert.That(route).IsEqualTo("/api/fs/find?query=todo");
    }

    [Test]
    public async Task FindEntries_Should_Carry_Every_Member_In_Wire_Order()
    {
        var route = OpenCodeRoutes.FileSystem.FindEntries(new FsFindRequest
        {
            Location = new LocationSelector { Directory = "/repo" },
            Query = "todo",
            Type = FsFindRequestType.Directory,
            Limit = 25,
        });

        await Assert.That(route).IsEqualTo("/api/fs/find?location[directory]=%2Frepo&query=todo&type=directory&limit=25");
    }

    [Test]
    public async Task FindEntries_Should_Refuse_A_Null_Request()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => _ = OpenCodeRoutes.FileSystem.FindEntries(null!));

        await Assert.That(exception.ParamName).IsEqualTo("request");
    }

    [Test]
    public async Task GetDiff_Should_Spell_The_Committed_Mode_And_Its_Base()
    {
        var route = OpenCodeRoutes.Vcs.GetDiff(new VcsDiffRequest { Mode = VcsMode.Committed, Base = "main" });

        await Assert.That(route).IsEqualTo("/api/vcs/diff?mode=committed&base=main");
    }

    public static IEnumerable<(VcsMode Mode, string WireValue)> PinnedModes() =>
    [
        (VcsMode.Working, "working"),
        (VcsMode.Branch, "branch"),
        (VcsMode.Committed, "committed"),
    ];

    [Test]
    [MethodDataSource(nameof(PinnedModes))]
    public async Task GetDiff_Should_Spell_Every_Mode_The_Way_The_Wire_Declares_It(VcsMode mode, string wireValue)
    {
        var route = OpenCodeRoutes.Vcs.GetDiff(new VcsDiffRequest { Mode = mode });

        await Assert.That(route).IsEqualTo($"/api/vcs/diff?mode={wireValue}");
    }

    /// <summary>A value the pinned server does not define is spelled as it was given (ADR-0035).</summary>
    [Test]
    public async Task GetDiff_Should_Spell_A_Mode_The_Pin_Does_Not_Define_As_Given()
    {
        var route = OpenCodeRoutes.Vcs.GetDiff(new VcsDiffRequest { Mode = new VcsMode("staged") });

        await Assert.That(route).IsEqualTo("/api/vcs/diff?mode=staged");
    }

    /// <summary>A default value has no spelling; the builder refuses it instead of sending an empty one.</summary>
    [Test]
    public async Task GetDiff_Should_Refuse_A_Default_Mode() =>
        _ = await Assert.That(() => OpenCodeRoutes.Vcs.GetDiff(new VcsDiffRequest { Mode = new VcsMode() })).Throws<ArgumentException>();

    [Test]
    public async Task GetStats_Should_Spell_The_Tools_Detail_Value()
    {
        var route = OpenCodeRoutes.Experimental.GetSessionStats(new ExperimentalSessionStatsRequest { Tools = ExperimentalSessionStatsRequestTools.Detail });

        await Assert.That(route).IsEqualTo("/api/experimental/session/stats?tools=detail");
    }

    [Test]
    public async Task GetStats_Should_Return_The_Bare_Path_When_Nothing_Is_Set()
    {
        await Assert.That(OpenCodeRoutes.Experimental.GetSessionStats(new ExperimentalSessionStatsRequest())).IsEqualTo("/api/experimental/session/stats");
        await Assert.That(OpenCodeRoutes.Experimental.GetSessionStats()).IsEqualTo("/api/experimental/session/stats");
    }

    /// <summary>
    /// The document declares 'cursor' and 'limit' as strings, but the server decodes both as
    /// non-negative integers, so they bind as numbers and travel as their decimal text. The byte
    /// cursor is a <see langword="long"/>: an offset past the 32-bit range reaches the wire whole.
    /// </summary>
    [Test]
    public async Task GetOutput_Should_Write_The_Cursor_And_Limit_As_Decimal_Text()
    {
        var route = OpenCodeRoutes.Shells.GetOutput("sh_100", new ShellOutputRequest
        {
            Location = new LocationSelector { Directory = "/repo" },
            Cursor = 3_000_000_000,
            Limit = 0,
        });

        await Assert.That(route).IsEqualTo("/api/shell/sh_100/output?location[directory]=%2Frepo&cursor=3000000000&limit=0");
    }

    /// <summary>
    /// The range is the server's to enforce (ADR-0014): a cursor it would refuse still reaches the
    /// wire as written.
    /// </summary>
    [Test]
    public async Task GetOutput_Should_Write_A_Cursor_Outside_The_Server_Range()
    {
        var route = OpenCodeRoutes.Shells.GetOutput("sh_100", new ShellOutputRequest { Cursor = -1 });

        await Assert.That(route).IsEqualTo("/api/shell/sh_100/output?cursor=-1");
    }

    [Test]
    public async Task GetOutput_Should_Return_The_Bare_Path_When_Nothing_Is_Set()
    {
        await Assert.That(OpenCodeRoutes.Shells.GetOutput("sh_100", new ShellOutputRequest()))
            .IsEqualTo("/api/shell/sh_100/output");
        await Assert.That(OpenCodeRoutes.Shells.GetOutput("sh_100")).IsEqualTo("/api/shell/sh_100/output");
    }

    [Test]
    public async Task Worktree_Routes_Should_Use_The_Project_Query_Only_For_Listing()
    {
        await Assert.That(OpenCodeRoutes.Worktrees.CreateWorktree).IsEqualTo("/api/worktree");
        await Assert.That(OpenCodeRoutes.Worktrees.ListWorktrees(new WorktreeListRequest { ProjectId = "prj 1" })).IsEqualTo("/api/worktree?projectID=prj%201");
        await Assert.That(OpenCodeRoutes.Worktrees.RefreshWorktrees).IsEqualTo("/api/worktree/refresh");
        await Assert.That(OpenCodeRoutes.Worktrees.RemoveWorktree).IsEqualTo("/api/worktree");
    }
}
