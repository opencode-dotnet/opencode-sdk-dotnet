using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness.Ownership;
using OpenCode.Sdk.Tests.Support;

using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Sessions.Support;

public sealed class PendingPermissionCleanupTests
{
    [Test]
    public async Task RejectAsync_Should_Fail_Cleanup_When_The_Test_Body_Succeeded()
    {
        var pending = new PendingPermissionCleanup(
            new ReplySessionClient(new SessionPermissionReplyResponse(500, null, "rejection failed")));
        var removed = false;
        var cleanup = new OwnedSessionCleanup(_ => Task.CompletedTask, _ =>
        {
            removed = true;
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(1), new OperationDeadlineScenario().Deadline);
        cleanup.Own("pending permission", token => pending.RejectAsync("per_owned", token));

        var thrown = await Assert.That(() => cleanup.CompleteAsync(null)).Throws<OpenCodeApiException>();

        await Assert.That(thrown!.Status).IsEqualTo(500);
        await Assert.That(removed).IsTrue();
    }

    [Test]
    [Arguments(400)]
    [Arguments(401)]
    [Arguments(500)]
    public async Task RejectAsync_Should_Report_Unexpected_Response_And_Continue_All_Cleanup(int status)
    {
        var response = new SessionPermissionReplyResponse(status, null, "rejection failed");
        var session = new ReplySessionClient(response);
        var pending = new PendingPermissionCleanup(session);
        var primary = new InvalidOperationException("primary failed");
        var removalFailure = new InvalidOperationException("session removal failed");
        var steps = new List<string>();
        var cleanup = new OwnedSessionCleanup(_ => Task.CompletedTask, _ =>
        {
            steps.Add("session");
            return Task.FromException(removalFailure);
        }, TimeSpan.FromSeconds(1), new OperationDeadlineScenario().Deadline);
        cleanup.Own("pending permission", token => pending.RejectAsync("per_owned", token));
        cleanup.Own("saved discovery", _ =>
        {
            steps.Add("discovery");
            return Task.CompletedTask;
        });
        cleanup.Own("saved removal", _ =>
        {
            steps.Add("saved");
            return Task.CompletedTask;
        });

        var thrown = await Assert.That(() => cleanup.CompleteAsync(primary)).Throws<InvalidOperationException>();

        await Assert.That(thrown).IsSameReferenceAs(primary);
        await Assert.That(steps).IsEquivalentTo(["discovery", "saved", "session"], CollectionOrdering.Matching);
        var failures = (AggregateException)primary.Data[OwnedCleanup.FailuresKey]!;
        await Assert.That(failures.InnerExceptions).Count().IsEqualTo(2);
        var rejection = failures.InnerExceptions[0] as OpenCodeApiException;
        await Assert.That(rejection).IsNotNull();
        await Assert.That(rejection!.Status).IsEqualTo(status);
        await Assert.That(rejection.RawBody).IsEqualTo(response.RawBody);
        await Assert.That(failures.InnerExceptions[1]).IsSameReferenceAs(removalFailure);
        await Assert.That(session.Reply).IsEqualTo(PermissionReply.Reject);
        await Assert.That(session.Options).IsSameReferenceAs(OpenCodeRequestOptions.NoThrow);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RejectAsync_Should_Accept_Success_Or_The_Owned_Already_Absent_Request(bool absent)
    {
        var response = absent
            ? new SessionPermissionReplyResponse(404,
                new PermissionNotFoundError { RequestId = "per_owned", Message = "not found" }, null)
            : new SessionPermissionReplyResponse { Status = 204 };
        var pending = new PendingPermissionCleanup(new ReplySessionClient(response));

        await pending.RejectAsync("per_owned", CancellationToken.None);
    }

    [Test]
    public async Task RejectAsync_Should_Reject_An_Unrelated_Not_Found_Envelope()
    {
        var error = new PermissionNotFoundError { RequestId = "per_other", Message = "not found" };
        var pending = new PendingPermissionCleanup(
            new ReplySessionClient(new SessionPermissionReplyResponse(404, error, null)));

        var thrown = await Assert.That(() => pending.RejectAsync("per_owned", CancellationToken.None))
            .Throws<OpenCodeApiException>();

        await Assert.That(thrown!.Status).IsEqualTo(404);
        await Assert.That(thrown.Error).IsSameReferenceAs(error);
        await Assert.That(thrown.Message).Contains("PermissionNotFoundError");
    }

    private sealed class ReplySessionClient(SessionPermissionReplyResponse response) : SessionClient
    {
        public PermissionReply? Reply { get; private set; }

        public OpenCodeRequestOptions? Options { get; private set; }

        public override Task<SessionPermissionReplyResponse> ReplyToPermissionAsync(
            string requestId, SessionPermissionReplyRequest request,
            OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            Reply = request.Decision;
            Options = requestOptions;
            return Task.FromResult(response);
        }
    }
}
