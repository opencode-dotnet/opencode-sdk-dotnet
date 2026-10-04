using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;

namespace OpenCode.Sdk.Tests.Sessions;

[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class SessionFormLiveTests(SimulatedDriveServerFixture server)
{
    private const string FieldKey = "response";
    private const string AnswerTitle = "SDK live answer";
    private const string CancelTitle = "SDK live cancel";
    private const string AnswerText = "accepted";
    private const string CancelMessage = "cancelled by the SDK live test";
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(15);

    [Test]
    [Timeout(180_000)]
    public async Task PostFormReplyAsync_Should_Persist_Answered_State_And_Reject_A_Second_Reply(
        CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var createdSession = await client.Sessions.CreateSessionAsync(new SessionCreateRequest
        {
            Title = "session-form-answer-live",
            Location = new LocationPublicRef { Directory = workspace.Path },
        }, cancellationToken: cancellationToken);
        var session = client.Sessions.GetSessionClient(createdSession.Session.Id);
        var formId = CreateOwnedFormId("answer");
        var cleanup = new OwnedSessionCleanup(session, CleanupTimeout);
        cleanup.MarkTurnCompleted();
        cleanup.Own("owned answer form", token => SettleFormForCleanupAsync(session, formId, token));
        Exception? primaryFailure = null;

        try
        {
            var created = await session.CreateFormAsync(new SessionFormCreateRequest
            {
                Id = formId,
                Title = AnswerTitle,
                Fields =
                [
                    new FormStringField
                    {
                        Key = FieldKey,
                        Required = true,
                        MinLength = 1,
                    },
                ],
            }, cancellationToken: cancellationToken);
            await Assert.That(created.Form.Id).IsEqualTo(formId);
            await Assert.That(created.Form.SessionId).IsEqualTo(createdSession.Session.Id);
            await Assert.That(created.Form.Title).IsEqualTo(AnswerTitle);
            await Assert.That(created.Form.Fields.Single()).IsTypeOf<FormStringField>();

            var sessionPending = await session.ListFormsAsync(cancellationToken: cancellationToken);
            var locationPending = await client.Forms.ListFormsAsync(cancellationToken: cancellationToken);
            await Assert.That(sessionPending.Forms.Any(form => form.Id == formId)).IsTrue();
            await Assert.That(locationPending.Forms.Any(form => form.Id == formId)).IsTrue();
            var pending = await session.GetFormAsync(formId, cancellationToken: cancellationToken);
            await Assert.That(pending.Form.State).IsTypeOf<FormStatePending>();

            var replyRequest = new SessionFormReplyRequest
            {
                Answer = new Dictionary<string, FormValue>(StringComparer.Ordinal)
                {
                    [FieldKey] = FormValue.FromText(AnswerText),
                },
            };
            var replied = await session.ReplyToFormAsync(formId, replyRequest, cancellationToken: cancellationToken);
            await Assert.That(replied.Status).IsEqualTo(204);
            var terminal = await session.GetFormAsync(formId, cancellationToken: cancellationToken);
            await Assert.That(terminal.Form.State).IsTypeOf<FormStateAnswered>();
            var answered = (FormStateAnswered)terminal.Form.State;
            await Assert.That(answered.Answer[FieldKey].Kind).IsEqualTo(FormValueKind.Text);
            await Assert.That(answered.Answer[FieldKey].Text).IsEqualTo(AnswerText);

            sessionPending = await session.ListFormsAsync(cancellationToken: cancellationToken);
            locationPending = await client.Forms.ListFormsAsync(cancellationToken: cancellationToken);
            await Assert.That(sessionPending.Forms.Any(form => form.Id == formId)).IsFalse();
            await Assert.That(locationPending.Forms.Any(form => form.Id == formId)).IsFalse();
            var repeated = await session.ReplyToFormAsync(
                formId, replyRequest, OpenCodeRequestOptions.NoThrow, cancellationToken);
            await Assert.That(repeated.Status).IsEqualTo(409);
            await Assert.That(repeated.Error).IsTypeOf<FormAlreadySettledError>();
            await Assert.That((repeated.Error as FormAlreadySettledError)?.Id).IsEqualTo(formId);

            Console.WriteLine(
                "session-form-answer-live: pending=observed answered=" + answered.Answer[FieldKey].Kind +
                " repeated-reply=" + Number(repeated.Status));
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

    [Test]
    [Timeout(180_000)]
    public async Task CancelFormAsync_Should_Persist_Cancelled_State_And_Report_Settled_And_Missing_Forms(
        CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var createdSession = await client.Sessions.CreateSessionAsync(new SessionCreateRequest
        {
            Title = "session-form-cancel-live",
            Location = new LocationPublicRef { Directory = workspace.Path },
        }, cancellationToken: cancellationToken);
        var session = client.Sessions.GetSessionClient(createdSession.Session.Id);
        var formId = CreateOwnedFormId("cancel");
        var missingFormId = CreateOwnedFormId("missing");
        var cleanup = new OwnedSessionCleanup(session, CleanupTimeout);
        cleanup.MarkTurnCompleted();
        cleanup.Own("owned cancel form", token => SettleFormForCleanupAsync(session, formId, token));
        Exception? primaryFailure = null;

        try
        {
            var created = await session.CreateFormAsync(new SessionFormCreateRequest
            {
                Id = formId,
                Title = CancelTitle,
                Fields =
                [
                    new FormStringField
                    {
                        Key = FieldKey,
                        Required = true,
                        MinLength = 1,
                    },
                ],
            }, cancellationToken: cancellationToken);
            await Assert.That(created.Status).IsEqualTo(200);
            await Assert.That(created.Form.Id).IsEqualTo(formId);
            await Assert.That(created.Form.SessionId).IsEqualTo(createdSession.Session.Id);
            await Assert.That(created.Form.Title).IsEqualTo(CancelTitle);
            await Assert.That(created.Form.Fields).Count().IsEqualTo(1);
            await Assert.That(created.Form.Fields[0]).IsTypeOf<FormStringField>();

            var cancelled = await session.CancelFormAsync(
                formId,
                new SessionFormCancelRequest { Message = CancelMessage },
                cancellationToken: cancellationToken);
            await Assert.That(cancelled.Status).IsEqualTo(204);
            var terminal = await session.GetFormAsync(formId, cancellationToken: cancellationToken);
            await Assert.That(terminal.Form.State).IsTypeOf<FormStateCancelled>();
            await Assert.That((terminal.Form.State as FormStateCancelled)?.Message).IsEqualTo(CancelMessage);

            var repeated = await session.CancelFormAsync(
                formId, requestOptions: OpenCodeRequestOptions.NoThrow, cancellationToken: cancellationToken);
            await Assert.That(repeated.Status).IsEqualTo(409);
            await Assert.That(repeated.Error).IsTypeOf<FormAlreadySettledError>();
            await Assert.That((repeated.Error as FormAlreadySettledError)?.Id).IsEqualTo(formId);

            var missing = await session.GetFormAsync(
                missingFormId, OpenCodeRequestOptions.NoThrow, cancellationToken);
            await Assert.That(missing.Status).IsEqualTo(404);
            await Assert.That(missing.Error).IsTypeOf<FormNotFoundError>();
            await Assert.That((missing.Error as FormNotFoundError)?.Id).IsEqualTo(missingFormId);

            Console.WriteLine(
                "session-form-cancel-live: cancelled=" + terminal.Form.State.Status +
                " repeated-cancel=" + Number(repeated.Status) +
                " missing=" + Number(missing.Status));
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

    private static string CreateOwnedFormId(string role) =>
        "frm_sdk_" + role + "_" + Guid.NewGuid().ToString("N");

    private static string Number(int value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static async Task SettleFormForCleanupAsync(
        SessionClient session,
        string formId,
        CancellationToken cancellationToken)
    {
        var response = await session.CancelFormAsync(
            formId, requestOptions: OpenCodeRequestOptions.NoThrow, cancellationToken: cancellationToken);
        if (response.Status is not (204 or 404 or 409))
        {
            throw new InvalidOperationException(
                "Owned form cleanup returned unexpected status " +
                response.Status.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
        }
    }
}
