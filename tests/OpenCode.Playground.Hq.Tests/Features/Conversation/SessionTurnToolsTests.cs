using OpenCode.Playground.Hq.Features.Conversation;
using OpenCode.Playground.Hq.Tests.Features.Conversation.Support;
using OpenCode.Sdk;
using OpenCode.Sdk.Models;
using TUnit.Assertions.Enums;
using static OpenCode.Playground.Hq.Tests.Features.Conversation.Support.ConversationScenario;

namespace OpenCode.Playground.Hq.Tests.Features.Conversation;

public sealed class SessionTurnToolsTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SwitchModelAsync_Should_Stop_The_Send_Or_Compact_On_Refusal(bool compact)
    {
        using var scenario = new ConversationScenario();
        await scenario.FollowAsync();
        scenario.MessageCalls.FailSwitch = true;
        scenario.EventCalls.Trace.Clear();
        var model = new ModelRef { Id = "chosen", ProviderId = "provider" };
        if (compact)
        {
            await scenario.Conversation.CompactAsync(model, CancellationToken.None);
        }
        else
        {
            await Assert.That(await scenario.Conversation.SendAsync("question", CancellationToken.None, model: model)).IsFalse();
        }
        await Assert.That(scenario.EventCalls.Trace).IsEquivalentTo(["switchModel"], CollectionOrdering.Matching);
        await Assert.That(scenario.Conversation.LastError).IsEqualTo(compact ? "Request failed" : "The prompt was not sent: offline");
    }

    [Test]
    public async Task FollowAsync_Should_Clear_The_Old_Conversation_When_Opening_The_Fork()
    {
        using var scenario = new ConversationScenario().WithHistory(User());
        await scenario.FollowAsync();
        _ = await scenario.Conversation.SendAsync("old prompt", CancellationToken.None);
        scenario.MessageCalls.Messages = [User("msg_fork", "fork history")];
        scenario.EventCalls.Items = [Connected];
        await scenario.Conversation.FollowAsync("ses_fork", CancellationToken.None);
        await Assert.That(scenario.Conversation.Lines.Select(static line => line.Text)).IsEquivalentTo(["fork history"], CollectionOrdering.Matching);
        await Assert.That(scenario.Conversation.SessionId).IsEqualTo("ses_fork");
    }

    [Test]
    public async Task Take_Should_Return_The_Prompt_Only_Once_For_That_Session()
    {
        var draft = new PromptDraft();
        draft.Set("ses_fork", "forked question");
        await Assert.That(draft.Take("ses_other")).IsNull();
        await Assert.That(draft.Take("ses_fork")).IsEqualTo("forked question");
        await Assert.That(draft.Take("ses_fork")).IsNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LoadChoicesAsync_Should_Filter_The_Location_Catalog_And_Initialize_Selections(bool saved)
    {
        using var scenario = new ConversationScenario();
        await scenario.FollowAsync();
        scenario.AgentCalls.Items = [Agent("sub", AgentInfoMode.Subagent), Agent("hidden", AgentInfoMode.Primary, true), Agent("plan", AgentInfoMode.All)];
        scenario.ModelCalls.Items = [Model("old") with { Status = ModelInfoStatus.Deprecated }, Model("unlisted", "other"), Model("chosen")];
        var model = new ModelRef { Id = "saved", ProviderId = "provider", Variant = "fast" };
        scenario.MessageCalls.Info = scenario.MessageCalls.Info with { Agent = saved ? "saved" : null, Model = saved ? model : null };

        await scenario.Conversation.LoadChoicesAsync(CancellationToken.None);

        await Assert.That(scenario.Conversation.Agents.Select(static agent => agent.Id)).IsEquivalentTo(["plan"], CollectionOrdering.Matching);
        await Assert.That(scenario.Conversation.Models.Values).IsEquivalentTo(["chosen (Provider name)"], CollectionOrdering.Matching);
        await Assert.That(scenario.Conversation.Agent).IsEqualTo(saved ? "saved" : "plan");
        await Assert.That(scenario.Conversation.Model).IsEqualTo(saved ? model : new ModelRef { Id = "chosen", ProviderId = "provider" });
        await Assert.That(scenario.AgentCalls.Request?.Location?.Directory).IsEqualTo("workspace");
        await Assert.That(scenario.ModelCalls.Request?.Location?.Directory).IsEqualTo("workspace");
        await Assert.That(scenario.ProviderCalls.Request?.Location?.Directory).IsEqualTo("workspace");
        await Assert.That(scenario.ModelCalls.DefaultRequest?.Location?.Directory).IsEqualTo(saved ? null : "workspace");
    }

    [Test]
    [Arguments("agent.updated", "workspace", true)]
    [Arguments("model.updated", "workspace", true)]
    [Arguments("provider.updated", "workspace", true)]
    [Arguments("agent.updated", "elsewhere", false)]
    public async Task FollowAsync_Should_Read_The_Choices_Again_When_The_Session_Folder_Announces_Its_Catalog(string type, string directory, bool reads)
    {
        using var scenario = new ConversationScenario();
        await scenario.FollowAsync();
        await scenario.Conversation.LoadChoicesAsync(CancellationToken.None);
        scenario.EventCalls.Items = [Connected, () =>
        {
            scenario.AgentCalls.Items = [Agent("plan", AgentInfoMode.Primary)];
            return Catalog(type, directory);
        }];

        await scenario.Conversation.FollowAsync(SessionId, CancellationToken.None);

        string[] expected = reads ? ["plan"] : [];
        await Assert.That(scenario.Conversation.Agents.Select(static agent => agent.Id)).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(scenario.Conversation.Agent).IsEqualTo(reads ? "plan" : null);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LoadChoicesAsync_Should_Not_Commit_A_Read_That_A_Newer_One_Replaced(bool fails)
    {
        using var scenario = new ConversationScenario();
        await scenario.FollowAsync();
        var hold = new TaskCompletionSource();
        scenario.AgentCalls.Hold = hold;
        var stale = scenario.Conversation.LoadChoicesAsync(CancellationToken.None);
        scenario.AgentCalls.Items = [Agent("plan", AgentInfoMode.Primary)];

        await scenario.Conversation.LoadChoicesAsync(CancellationToken.None);
        if (fails)
        {
            hold.SetException(new OpenCodeTransportException("offline"));
        }
        else
        {
            hold.SetResult();
        }
        await stale;

        await Assert.That(scenario.Conversation.Agents.Select(static agent => agent.Id)).IsEquivalentTo(["plan"], CollectionOrdering.Matching);
        await Assert.That(scenario.Conversation.Agent).IsEqualTo("plan");
        await Assert.That(scenario.Conversation.LastError).IsNull();
    }

    [Test]
    [Arguments(true, false)]
    [Arguments(false, false)]
    [Arguments(true, true)]
    public async Task SendAsync_Should_Switch_Agent_Then_Model_Then_Prompt_Or_Stop_On_Refusal(bool different, bool refused)
    {
        using var scenario = new ConversationScenario();
        await scenario.FollowAsync();
        var model = new ModelRef { Id = "chosen", ProviderId = "provider" };
        scenario.MessageCalls.Info = scenario.MessageCalls.Info with { Agent = different ? "old" : "plan", Model = different ? null : model };
        scenario.MessageCalls.FailSwitch = refused;
        scenario.EventCalls.Trace.Clear();

        var sent = await scenario.Conversation.SendAsync("question", CancellationToken.None, "plan", model);

        string[] expected = different ? ["switchAgent", "switchModel", "prompt"] : ["prompt"];
        if (refused)
        {
            expected = ["switchAgent"];
        }
        await Assert.That(scenario.EventCalls.Trace).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(sent).IsEqualTo(!refused);
        await Assert.That(scenario.Conversation.LastError).IsEqualTo(refused ? "The prompt was not sent: offline" : null);
        await Assert.That(scenario.MessageCalls.Prompt?.Text).IsEqualTo(refused ? null : "question");
        if (different)
        {
            await Assert.That(scenario.MessageCalls.SwitchAgent?.Agent).IsEqualTo("plan");
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task CompactAsync_Should_Switch_A_Different_Model_First_And_Report_A_Refusal(bool different, bool refused)
    {
        using var scenario = new ConversationScenario();
        await scenario.FollowAsync();
        var model = new ModelRef { Id = "chosen", ProviderId = "provider", Variant = "fast" };
        scenario.MessageCalls.Info = scenario.MessageCalls.Info with { Model = different ? null : model };
        scenario.MessageCalls.ToolFailure = refused ? new OpenCodeTransportException("offline") : null;
        scenario.EventCalls.Trace.Clear();

        await scenario.Conversation.CompactAsync(model, CancellationToken.None);

        string[] expected = different ? ["switchModel", "compact"] : ["compact"];
        await Assert.That(scenario.EventCalls.Trace).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(scenario.MessageCalls.Compact?.Id.Value).Matches("^msg_[0-9a-f]{32}$");
        if (different)
        {
            await Assert.That(scenario.MessageCalls.SwitchModel?.Model).IsEqualTo(model);
        }
        await Assert.That(scenario.Conversation.LastError).IsEqualTo(refused ? "Request failed" : null);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ForkAsync_Should_Fork_Before_The_Message_Or_Report_The_Refusal(bool refused)
    {
        using var scenario = new ConversationScenario();
        await scenario.FollowAsync();
        scenario.MessageCalls.ToolFailure = refused ? new OpenCodeTransportException("offline") : null;

        var fork = await scenario.Conversation.ForkAsync("msg_chosen", CancellationToken.None);

        await Assert.That(scenario.MessageCalls.Fork?.Before.Value).IsEqualTo("msg_chosen");
        await Assert.That(fork?.Id).IsEqualTo(refused ? null : "ses_fork");
        await Assert.That(scenario.Conversation.LastError).IsEqualTo(refused ? "Request failed" : null);
    }
}
