using System.Text.Json;

namespace OpenCode.Sdk.Tests.Harness;

/// <summary>
/// An <c>llm.request</c> notification: the pending provider invocation the controller answers
/// through chunk/finish, the provider route it targets, the model the provider request named
/// (null when the provider-shaped body carries none), and the cloned request body itself, so a
/// proof can require the real content the model loop resumed with.
/// </summary>
internal sealed record DriveInvocation(string Id, string Url, string? Model, JsonElement Body);
