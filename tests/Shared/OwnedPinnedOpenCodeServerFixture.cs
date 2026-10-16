using TUnit.Core.Interfaces;

namespace OpenCode.Sdk.Tests.Harness;

/// <summary>Owns isolated configuration for tests whose operations must never discover ambient package plugins.</summary>
public sealed class OwnedPinnedOpenCodeServerFixture : IAsyncInitializer, IAsyncDisposable, ITestEndEventReceiver
{
    private readonly PinnedOpenCodeServerFixture _fixture = new(forceOwned: true);

    public int Order => _fixture.Order;

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public OpenCodeClient CreateClient() => _fixture.CreateClient();

    public ValueTask OnTestEnd(TestContext context) => _fixture.OnTestEnd(context);

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
