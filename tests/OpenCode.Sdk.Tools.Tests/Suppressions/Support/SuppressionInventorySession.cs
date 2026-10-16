using OpenCode.Sdk.Tools.Generator.Refresh;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions;
using TUnit.Core.Interfaces;

namespace OpenCode.Sdk.Tools.Tests.Suppressions.Support;

/// <summary>One completed suppression inventory shared by the repository's ledger checks.</summary>
public sealed class SuppressionInventorySession : IAsyncInitializer
{
    private SuppressionInventory? _inventory;

    internal SuppressionInventory Inventory => _inventory ?? throw new InvalidOperationException("The inventory is not initialized.");

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        var fileSystem = new RealFileSystem();
        var root = new RepositoryRoot(fileSystem).Locate();
        // TUnit initialization has no cancellation token; the fixture outlives any one test.
        var files = await new RepositoryFileSet(fileSystem).EnumerateAsync(root, new CliWrapProcessRunner(), CancellationToken.None);
        _inventory = await new SuppressionScan(fileSystem, files).ReadAsync(root, CancellationToken.None);
    }
}
