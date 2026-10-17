using System.Net;

namespace OpenCode.Sdk.Tests.Common.Support;

public readonly record struct DeclaredErrorCase(
    string CaseId,
    DeclaredErrorDoor Door,
    HttpStatusCode Status,
    string WireBody,
    string Tag,
    Type ErrorType,
    string JsonPointer)
{
    public override string ToString() => CaseId + " " + Door.MemberName;
}
