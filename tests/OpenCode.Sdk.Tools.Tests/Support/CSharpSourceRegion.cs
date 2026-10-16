using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace OpenCode.Sdk.Tools.Tests.Support;

internal sealed record CSharpSourceRegion(CompilationUnitSyntax Root, int LineOffset);
