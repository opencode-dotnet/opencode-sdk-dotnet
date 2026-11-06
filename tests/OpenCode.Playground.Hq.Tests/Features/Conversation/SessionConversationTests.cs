using OpenCode.Playground.Hq.Tests.Features.Conversation.Support;
using TUnit.Assertions.Enums;
using static OpenCode.Playground.Hq.Tests.Features.Conversation.Support.ConversationScenario;

namespace OpenCode.Playground.Hq.Tests.Features.Conversation;

public sealed class SessionConversationTests
{
    [Test]
    public async Task FollowAsync_Should_Read_History_After_The_Subscription_Opens()
    {
        using var scenario = new ConversationScenario();
        await scenario.FollowAsync();
        await Assert.That(scenario.EventCalls.Trace).IsEquivalentTo(["subscription", "history"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task FollowAsync_Should_Apply_Only_This_Sessions_Events()
    {
        using var scenario = new ConversationScenario();
        var observed = new List<string>();
        scenario.Conversation.Changed += (_, _) => observed.Add(
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{string.Join(" | ", scenario.Conversation.Lines.Select(static line => line.Text))}; {scenario.Conversation.Running}; {scenario.Conversation.EndedTurns}"));
        await scenario.FollowAsync(
            Delta(0, "other", "ses_other"), Started(),
            Started("ses_other"), Delta(0, "own"),
            Succeeded("ses_other"), Ended(0, "own final"), Ended(0, "other final", "ses_other"), Succeeded());
        await Assert.That(observed).IsEquivalentTo(
            ["; False; 0", "; True; 0", "own; True; 0", "own final; True; 0", "own final; False; 1"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task FollowAsync_Should_Load_Only_User_And_Assistant_Text_Oldest_First()
    {
        using var scenario = new ConversationScenario().WithHistory(Assistant(Text("answer"), Reasoning(), Tool()), SystemMessage(), User());
        await scenario.FollowAsync();
        await Assert.That(scenario.Conversation.Lines.Select(static line => $"{line.Author}: {line.Text}"))
            .IsEquivalentTo(["you: question", "assistant: answer"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task FollowAsync_Should_Key_Stored_Text_Ordinals_Without_Reasoning()
    {
        using var scenario = new ConversationScenario().WithHistory(Assistant(Text("first"), Reasoning(), Text("second")));
        var observed = new List<string>();
        scenario.Conversation.Changed += (_, _) => observed.Add(string.Join(" | ", scenario.Conversation.Lines.Select(static line => line.Text)));
        scenario.EventCalls.Items = [Connected, () => Delta(1, " part"), () => Ended(1, "final")];
        await scenario.Conversation.FollowAsync(SessionId, CancellationToken.None);
        await Assert.That(observed).IsEquivalentTo(["first | second", "first | second part", "first | final"], CollectionOrdering.Matching);
        await Assert.That(scenario.Conversation.Lines.Select(static line => line.Text)).IsEquivalentTo(["first", "final"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task FollowAsync_Should_Append_Deltas_And_Replace_With_Ended_Text()
    {
        using var scenario = new ConversationScenario();
        var observed = new List<string>();
        scenario.Conversation.Changed += (_, _) => observed.Add(string.Join(" | ", scenario.Conversation.Lines.Where(static line => line.Author == "assistant").Select(static line => line.Text)));
        await scenario.FollowAsync(Delta(0, "Hel"), Delta(0, "lo"), Ended(0, "Hello."));
        await Assert.That(observed).IsEquivalentTo(["", "Hel", "Hello", "Hello."], CollectionOrdering.Matching);
        await scenario.Conversation.SendAsync("prompt", CancellationToken.None);
        await Assert.That(scenario.MessageCalls.Prompt?.Id.Value).Matches("^msg_[0-9a-f]{32}$");
        await Assert.That(scenario.Conversation.Lines.Where(static line => line.Author == "you").Select(static line => line.Text)).IsEquivalentTo(["prompt"], CollectionOrdering.Matching);
        await Assert.That(scenario.Conversation.Lines.Where(static line => line.Author == "assistant").Select(static line => line.Text))
            .IsEquivalentTo(["Hello."], CollectionOrdering.Matching);
    }

    [Test]
    public async Task FollowAsync_Should_Keep_Two_Ordinals_As_Two_Lines()
    {
        using var scenario = new ConversationScenario();
        await scenario.FollowAsync(Delta(1, "second"), Delta(0, "first"), Ended(1, "second final"));
        await Assert.That(scenario.Conversation.Lines.Select(static line => line.Text)).IsEquivalentTo(["second final", "first"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task FollowAsync_Should_Show_Text_From_An_Already_Running_Session()
    {
        using var scenario = new ConversationScenario();
        scenario.SessionCalls.Running = true;
        await scenario.FollowAsync(Delta(0, "already running"));
        await Assert.That(scenario.Conversation.Running).IsTrue();
        await Assert.That(scenario.Conversation.Lines.Select(static line => line.Text)).IsEquivalentTo(["already running"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task FollowAsync_Should_Stop_And_Report_The_Failed_Turn()
    {
        using var scenario = new ConversationScenario();
        scenario.SessionCalls.Running = true;
        await scenario.FollowAsync(Failed());
        await Assert.That(scenario.Conversation.Running).IsFalse();
        await Assert.That(scenario.Conversation.LastError).IsEqualTo("The turn failed: provider.free-limit: limit reached");
    }

    [Test]
    public async Task FollowAsync_Should_Count_Each_Ended_Turn_Even_Between_Renders()
    {
        using var scenario = new ConversationScenario();
        var running = new List<bool>();
        var ended = new List<int>();
        scenario.Conversation.Changed += (_, _) =>
        {
            running.Add(scenario.Conversation.Running);
            ended.Add(scenario.Conversation.EndedTurns);
        };
        await scenario.FollowAsync(Started(), Succeeded(), Started(), Failed(), Started(), Interrupted());
        await Assert.That(running).IsEquivalentTo([false, true, false, true, false, true, false], CollectionOrdering.Matching);
        await Assert.That(ended).IsEquivalentTo([0, 0, 1, 1, 2, 2, 3], CollectionOrdering.Matching);
    }

    [Test]
    public async Task SendAsync_Should_Report_A_Failure_Without_Throwing()
    {
        using var scenario = new ConversationScenario();
        await scenario.FollowAsync();
        scenario.MessageCalls.FailPrompt = true;
        await Assert.That(() => scenario.Conversation.SendAsync("prompt", CancellationToken.None)).ThrowsNothing();
        await Assert.That(scenario.Conversation.LastError).IsEqualTo("The prompt was not sent: unauthorized");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SendAsync_Should_Show_The_Prompt_Until_The_Server_Takes_It(bool reconnect)
    {
        using var scenario = new ConversationScenario();
        await scenario.FollowAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.MessageCalls.PromptRelease = release;
        var send = scenario.Conversation.SendAsync("prompt", CancellationToken.None);
        try
        {
            if (reconnect)
            {
                await scenario.FollowAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }

            await Assert.That(scenario.Conversation.Lines.Select(static line => (line.Author, line.Text, line.Pending)))
                .IsEquivalentTo([("you", "prompt", true)], CollectionOrdering.Matching);
            await Assert.That(scenario.Conversation.Running).IsTrue();
        }
        finally
        {
            _ = release.TrySetResult();
            _ = await send.WaitAsync(TimeSpan.FromSeconds(30));
        }

        await Assert.That(scenario.Conversation.Lines.Select(static line => (line.Author, line.Text, line.Pending)))
            .IsEquivalentTo([("you", "prompt", false)], CollectionOrdering.Matching);
    }

    [Test]
    public async Task SendAsync_Should_Take_Back_A_Refused_Prompt()
    {
        using var scenario = new ConversationScenario();
        await scenario.FollowAsync().WaitAsync(TimeSpan.FromSeconds(30));
        scenario.MessageCalls.FailPrompt = true;
        var sent = await scenario.Conversation.SendAsync("prompt", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(sent).IsFalse();
        await Assert.That(scenario.Conversation.Lines).IsEmpty();
        await Assert.That(scenario.Conversation.Running).IsFalse();
    }

    [Test]
    public async Task SendAsync_Should_Keep_Live_Text_On_Its_Line_After_A_Refused_Prompt()
    {
        using var scenario = new ConversationScenario();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.MessageCalls.PromptRelease = release;
        scenario.MessageCalls.FailPrompt = true;
        var send = scenario.Conversation.SendAsync("prompt", CancellationToken.None);
        try
        {
            // Another client's turn streams after the pending line; taking that line back must not move the live text.
            await scenario.FollowAsync(Delta(0, "Hel")).WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            _ = release.TrySetResult();
            _ = await send.WaitAsync(TimeSpan.FromSeconds(30));
        }

        scenario.EventCalls.Items = [() => Delta(0, "lo")];
        await scenario.Conversation.FollowAsync(SessionId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(scenario.Conversation.Lines.Select(static line => (line.Author, line.Text)))
            .IsEquivalentTo([("assistant", "Hello")], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SendAsync_Should_Keep_A_Prompt_The_Server_Took_Before_The_Call_Failed(bool storedBeforeFailure)
    {
        using var scenario = new ConversationScenario();
        await scenario.FollowAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.MessageCalls.PromptRelease = release;
        scenario.MessageCalls.FailPrompt = true;
        var send = scenario.Conversation.SendAsync("prompt", CancellationToken.None);
        var promptId = scenario.MessageCalls.Prompt?.Id.Value ?? "";
        bool sent;
        try
        {
            // The echo and then a reload before the history holds the prompt, or a reload whose history already holds it.
            if (storedBeforeFailure)
            {
                _ = scenario.WithHistory(User(promptId, "prompt"));
            }
            else
            {
                await scenario.FollowAsync(Enqueued(promptId)).WaitAsync(TimeSpan.FromSeconds(30));
            }

            await scenario.FollowAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await Assert.That(scenario.Conversation.Running).IsTrue();
        }
        finally
        {
            _ = release.TrySetResult();
            sent = await send.WaitAsync(TimeSpan.FromSeconds(30));
        }

        await Assert.That(sent).IsTrue();
        await Assert.That(scenario.Conversation.Lines.Select(static line => (line.Author, line.Text, line.Pending)))
            .IsEquivalentTo([("you", "prompt", false)], CollectionOrdering.Matching);
        await Assert.That(scenario.Conversation.LastError).IsNull();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task SendAsync_Should_Settle_A_Prompt_That_Ends_While_A_Reload_Waits(bool refused, bool stored)
    {
        using var scenario = new ConversationScenario();
        await scenario.FollowAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var prompt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.MessageCalls.PromptRelease = prompt;
        scenario.MessageCalls.FailPrompt = refused;
        var send = scenario.Conversation.SendAsync("prompt", CancellationToken.None);
        if (stored)
        {
            _ = scenario.WithHistory(User(scenario.MessageCalls.Prompt?.Id.Value ?? "", "prompt"));
        }

        scenario.SessionCalls.ActiveStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.SessionCalls.ActiveRelease = active;
        var reload = scenario.FollowAsync();
        try
        {
            await scenario.SessionCalls.ActiveStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            _ = prompt.TrySetResult();
        }
        finally
        {
            _ = prompt.TrySetResult();
            _ = await send.WaitAsync(TimeSpan.FromSeconds(30));
            _ = active.TrySetResult();
            await reload.WaitAsync(TimeSpan.FromSeconds(30));
        }

        await Assert.That(scenario.Conversation.Lines.Select(static line => (line.Author, line.Text, line.Pending)))
            .IsEquivalentTo(refused && !stored ? Array.Empty<(string, string, bool)>() : [("you", "prompt", false)], CollectionOrdering.Matching);
        await Assert.That(scenario.Conversation.LastError).IsEqualTo(refused && !stored ? "The prompt was not sent: unauthorized" : null);
    }

    [Test]
    public async Task SendAsync_Should_Leave_A_Running_Turn_Running_When_A_Prompt_Is_Refused()
    {
        using var scenario = new ConversationScenario();
        await scenario.FollowAsync(Started()).WaitAsync(TimeSpan.FromSeconds(30));
        scenario.MessageCalls.FailPrompt = true;
        var sent = await scenario.Conversation.SendAsync("prompt", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(sent).IsFalse();
        await Assert.That(scenario.Conversation.Running).IsTrue();
    }

    [Test]
    public async Task FollowAsync_Should_Be_Ready_Only_After_Messages_And_Active_State_Load()
    {
        using var scenario = new ConversationScenario().WithHistory(User());
        var messagesStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.MessageCalls.MessagesStarted = messagesStarted;
        scenario.SessionCalls.ActiveStarted = activeStarted;
        scenario.MessageCalls.MessagesRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.SessionCalls.ActiveRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await Assert.That(scenario.Conversation.Ready).IsFalse();
        var follow = scenario.FollowAsync();
        try
        {
            await messagesStarted.Task;
            await Assert.That(scenario.Conversation.Ready).IsFalse();
            scenario.MessageCalls.MessagesRelease.SetResult();
            await activeStarted.Task;
            await Assert.That(scenario.Conversation.Ready).IsFalse();
        }
        finally
        {
            scenario.MessageCalls.MessagesRelease.TrySetResult();
            scenario.SessionCalls.ActiveRelease.TrySetResult();
            await follow;
        }

        await Assert.That(scenario.Conversation.Ready).IsTrue();
    }

    [Test]
    public async Task FollowAsync_Should_Reload_Messages_And_Active_State_On_Reconnect()
    {
        using var scenario = new ConversationScenario().WithHistory(User());
        scenario.EventCalls.Items = [Connected, () =>
        {
            scenario.WithHistory(Assistant(Text("missed answer")));
            scenario.SessionCalls.Running = true;
            return Connected();
        }];
        await scenario.Conversation.FollowAsync(SessionId, CancellationToken.None);
        await Assert.That(scenario.Conversation.Lines.Select(static line => line.Text)).IsEquivalentTo(["missed answer"], CollectionOrdering.Matching);
        await Assert.That(scenario.Conversation.Running).IsTrue();
    }
}
