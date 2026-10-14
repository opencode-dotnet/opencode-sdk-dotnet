namespace OpenCode.Sdk.Tools.Tests.Packaging;

internal sealed record CompatibilitySuppression(string DiagnosticId, string Target, string Left, string Right, bool IsBaselineSuppression);
