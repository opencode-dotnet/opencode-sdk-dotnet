namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Support;

internal sealed record ServiceConfigEditCase(IReadOnlyList<string> Command, int Stops);
