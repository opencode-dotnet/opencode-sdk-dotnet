using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;

namespace OpenCode.Sdk.Tests.Sessions;

/// <summary>
/// The compile-and-run home of the getting-started guide's "wait for the session to go idle, then
/// read its messages" recipe: the guide's calls, in the
/// guide's order, against the simulated server, with the Drive controller answering the model
/// request the prompt starts. Session creation names no model, as the guide's does, so the turn
/// reaching the simulated model also proves a model-less session gets the configured default.
/// </summary>
/// <remarks>
/// Two differences from the guide, both forced by the harness. The session is placed in an owned
/// workspace: the simulated host runs from the upstream CLI package, so a location-less session
/// would live in upstream's own project. The wait is started before the turn is driven and bounded
/// by the test's own deadline instead of the guide's five minutes, because the model request only
/// answers once the controller scripts it. Nothing extracts the guide's text: a change to the
/// recipe changes this test by review.
/// </remarks>
[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class GettingStartedRecipeLiveTests(SimulatedDriveServerFixture server)
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RequestWait = TimeSpan.FromSeconds(60);

    [Test]
    [Timeout(180_000)]
    public async Task Prompt_Then_Wait_Then_Messages_Should_Read_The_Assistant_Reply(CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var reply = "Getting started reply " + Guid.NewGuid().ToString("N") + ".";

        var created = await client.Sessions.CreateSessionAsync(
            new SessionCreateRequest
            {
                Title = "hello from .NET",
                Location = new LocationPublicRef { Directory = workspace.Path },
            },
            cancellationToken: cancellationToken);
        var session = client.Sessions.GetSessionClient(created.Session.Id);
        var cleanup = new OwnedSessionInboxCleanup(
            session, client.Experimental, created.Session.Id, server.Controller, CleanupTimeout);
        Exception? primaryFailure = null;

        try
        {
            var prompt = await session.PromptAsync(
                new SessionPromptRequest { Text = "Summarize this repository." },
                cancellationToken: cancellationToken);
            await Assert.That(prompt.Status).IsEqualTo(200);

            var wait = client.Experimental.WaitForSessionAsync(created.Session.Id, cancellationToken: cancellationToken);
            cleanup.RetainWait(wait);

            var invocation = cleanup.RetainInvocation(await server.Controller.WaitForRequestAsync(RequestWait));
            await Assert.That(invocation.Invocation.Model).IsEqualTo(SimulationConfigSeed.ModelId);
            await Assert.That(invocation.Invocation.Url).IsEqualTo(SimulationConfigSeed.ChatCompletionsUrl);
            await invocation.ChunkTextAsync(reply);
            await invocation.FinishAsync();

            var waited = await wait;
            await Assert.That(waited.Status).IsEqualTo(204);

            var texts = new List<string>();
            await foreach (var message in session.EnumerateMessagesAsync(
                               new SessionMessageListRequest { Order = ListOrder.Ascending }, cancellationToken))
            {
                if (message is SessionMessageAssistant assistant)
                {
                    foreach (var text in assistant.Content.OfType<SessionMessageAssistantText>())
                    {
                        texts.Add(text.Text);
                    }
                }
            }

            await Assert.That(texts).IsEquivalentTo([reply]);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }
        finally
        {
            await cleanup.CompleteAsync(primaryFailure);
        }
    }
}
