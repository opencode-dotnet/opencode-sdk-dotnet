using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// The readiness poll's error rule: the server's declared "not available yet" answer
/// (<see cref="ServiceUnavailableError"/>) is waited through, every other error ends the wait at
/// once, and a budget that ends while the server still answers 503 says so.
/// </summary>
public sealed class LiveReadinessTests
{
    private const string Description = "the seeded models";

    [Test]
    public async Task WaitAsync_Should_Poll_Through_A_Declared_ServiceUnavailableError()
    {
        var observations = 0;

        var result = await LiveReadiness.WaitAsync(
            _ => ++observations < 3 ? throw Unavailable() : Task.FromResult("listed"),
            static value => value == "listed",
            Description,
            CancellationToken.None);

        await Assert.That(result).IsEqualTo("listed");
        await Assert.That(observations).IsEqualTo(3);
    }

    [Test]
    public async Task WaitAsync_Should_End_At_Once_On_Any_Other_Api_Error()
    {
        var observations = 0;
        var conflict = new OpenCodeApiException(
            "The opencode API returned status 409.", 409, new ConflictError { Message = "busy" }, rawBody: null);

        var thrown = await Assert.ThrowsAsync<OpenCodeApiException>(() => LiveReadiness.WaitAsync<string>(
            _ =>
            {
                observations++;
                throw conflict;
            },
            static _ => true,
            Description,
            CancellationToken.None));

        await Assert.That(thrown).IsSameReferenceAs(conflict);
        await Assert.That(observations).IsEqualTo(1);
    }

    /// <summary>The budget is the poll's own bound; the verdict is that the server never stopped answering 503.</summary>
    [Test]
    public async Task WaitAsync_Should_Name_The_Declared_503_When_The_Budget_Ends()
    {
        var thrown = await Assert.ThrowsAsync<TimeoutException>(() => LiveReadiness.WaitAsync<string>(
            _ => throw Unavailable(),
            static _ => true,
            Description,
            describe: null,
            TimeSpan.FromMilliseconds(300),
            CancellationToken.None));

        await Assert.That(thrown!.Message).Contains("Timed out waiting for " + Description);
        await Assert.That(thrown.Message).EndsWith("; the last answer was 503 ServiceUnavailableError.");
        await Assert.That(thrown.InnerException).IsTypeOf<OpenCodeApiException>();
    }

    private static OpenCodeApiException Unavailable() =>
        new("The opencode API returned status 503 ('ServiceUnavailableError').", 503,
            new ServiceUnavailableError { Message = "the catalog is not ready" }, rawBody: null);
}
