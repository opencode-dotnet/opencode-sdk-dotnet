using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

public sealed class OperationIdentityPolicyTests
{
    [Test]
    public async Task BuildMap_Should_Check_The_Reason_Even_When_The_Subject_Is_Blank()
    {
        var exception = Assert.Throws<BindingException>(() => OperationIdentityPolicy.BuildMap(
            IdentityCuration(OperationIdentity(" ", "pty.list", reason: " "))));

        await Assert.That(exception.Errors.Select(static error => error.Subject + ": " + error.Problem))
            .IsEquivalentTo([
                "operationIdentities[0]: operationIdentities[0] curation names no operationId",
                "operationIdentities[0]: operation identity curation must declare a reason",
            ], CollectionOrdering.Matching);
    }

    [Test]
    public async Task BuildMap_Should_Return_The_Mapped_Identities()
    {
        var curation = IdentityCuration(
            OperationIdentity("server.experimental.persistentPty.list", "persistentPty.list"),
            OperationIdentity("server.experimental.persistentPty.get", "persistentPty.get"));

        var map = OperationIdentityPolicy.BuildMap(curation);

        await Assert.That(map).Count().IsEqualTo(2);
        await Assert.That(map["server.experimental.persistentPty.list"]).IsEqualTo("persistentPty.list");
        await Assert.That(map["server.experimental.persistentPty.get"]).IsEqualTo("persistentPty.get");
    }

    [Test]
    public async Task BuildMap_Should_Admit_A_Repair_For_Any_Leaked_Upstream_Group()
    {
        var curation = IdentityCuration(OperationIdentity("server.session.archive", "session.archive"));

        var map = OperationIdentityPolicy.BuildMap(curation);

        await Assert.That(map["server.session.archive"]).IsEqualTo("session.archive");
    }

    [Test]
    public async Task BuildMap_Should_Refuse_A_Row_Without_A_Reason()
    {
        var curation = IdentityCuration(OperationIdentity("server.experimental.pty.list", "pty.list", reason: " "));

        var exception = Assert.Throws<BindingException>(() => _ = OperationIdentityPolicy.BuildMap(curation));

        var error = exception.Errors.Single();
        await Assert.That(error.Subject).IsEqualTo("server.experimental.pty.list");
        await Assert.That(error.Problem).IsEqualTo("operation identity curation must declare a reason");
    }

    [Test]
    public async Task BuildMap_Should_Refuse_A_Subject_That_Already_Satisfies_The_Convention()
    {
        var curation = IdentityCuration(OperationIdentity("experimental.session.export", "session.export"));

        var exception = Assert.Throws<BindingException>(() => _ = OperationIdentityPolicy.BuildMap(curation));

        var error = exception.Errors.Single();
        await Assert.That(error.Subject).IsEqualTo("experimental.session.export");
        await Assert.That(error.Problem).Contains("already satisfies the protocol convention");
    }

    [Test]
    public async Task BuildMap_Should_Refuse_A_Malformed_Intended_Identity()
    {
        var curation = IdentityCuration(OperationIdentity("server.experimental.pty.list", "pty..list"));

        var exception = Assert.Throws<BindingException>(() => _ = OperationIdentityPolicy.BuildMap(curation));

        var error = exception.Errors.Single();
        await Assert.That(error.Problem).Contains("pty..list");
        await Assert.That(error.Problem).Contains("must satisfy the protocol convention");
    }

    [Test]
    public async Task BuildMap_Should_Refuse_A_Duplicated_Subject()
    {
        var curation = IdentityCuration(
            OperationIdentity("server.experimental.pty.list", "pty.list"),
            OperationIdentity("server.experimental.pty.list", "ptys.list"));

        var exception = Assert.Throws<BindingException>(() => _ = OperationIdentityPolicy.BuildMap(curation));

        var error = exception.Errors.Single(static error => error.Problem.Contains("duplicated", StringComparison.Ordinal));
        await Assert.That(error.Subject).IsEqualTo("server.experimental.pty.list");
    }

    [Test]
    public async Task BuildMap_Should_Refuse_An_Intended_Identity_Claimed_Twice()
    {
        var curation = IdentityCuration(
            OperationIdentity("server.experimental.pty.list", "pty.list"),
            OperationIdentity("server.experimental.ptys.list", "pty.list"));

        var exception = Assert.Throws<BindingException>(() => _ = OperationIdentityPolicy.BuildMap(curation));

        var error = exception.Errors.Single();
        await Assert.That(error.Subject).IsEqualTo("server.experimental.ptys.list");
        await Assert.That(error.Problem).Contains("claimed by more than one identity row");
    }

    private static GenerationCuration IdentityCuration(params OperationIdentityCuration[] rows) =>
        Curation(new Dictionary<string, GroupCuration>(StringComparer.Ordinal), operationIdentities: rows);
}
