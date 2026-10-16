using System.Text.Json;
using System.Text.RegularExpressions;
using OpenCode.Sdk.Tests.Harness;
using Testably.Abstractions.Testing;

namespace OpenCode.Sdk.Tests;

public sealed class DriveManifestTests
{
    /// <summary>
    /// The regex source generator's <c>GeneratedRegexAttribute</c> is unavailable on this
    /// project's net472 leg, so a single static <see cref="Regex"/> with an explicit match
    /// timeout is the shape that compiles identically across every target framework this file
    /// builds for.
    /// </summary>
    private static readonly Regex InstanceNamePattern =
        new("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$", RegexOptions.None, TimeSpan.FromSeconds(1));

    /// <summary>
    /// MockFileSystem simulates the host OS, so the registry path must stay portable across
    /// the Windows and Linux/macOS test legs.
    /// </summary>
    private static string RegistryDirectory(MockFileSystem fileSystem) =>
        fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "drive-instances");

    [Test]
    public async Task Write_Should_Register_A_Loopback_Manifest_Under_The_Instance_Name()
    {
        var fileSystem = new MockFileSystem();
        var registry = RegistryDirectory(fileSystem);

        var manifest = DriveManifest.Write(fileSystem, registry);

        var path = fileSystem.Path.Combine(registry, manifest.InstanceName + ".json");
        await Assert.That(fileSystem.File.Exists(path)).IsTrue();
        using var document = JsonDocument.Parse(fileSystem.File.ReadAllText(path));
        var endpoints = document.RootElement.GetProperty("endpoints");
        await Assert.That(endpoints.GetProperty("backend").GetString())
            .IsEqualTo(manifest.BackendEndpoint.AbsoluteUri.TrimEnd('/'));
        await Assert.That(endpoints.GetProperty("ui").GetString())
            .IsEqualTo(manifest.UiEndpoint.AbsoluteUri.TrimEnd('/'));
    }

    [Test]
    public async Task Write_Should_Generate_An_Upstream_Legal_Instance_Name()
    {
        var fileSystem = new MockFileSystem();

        var manifest = DriveManifest.Write(fileSystem, RegistryDirectory(fileSystem));

        // The upstream instance-name filter (manifest.ts:6-10 at the pin).
        await Assert.That(InstanceNamePattern.IsMatch(manifest.InstanceName)).IsTrue();
    }

    [Test]
    public async Task Write_Should_Reserve_Two_Distinct_Loopback_Ports()
    {
        var fileSystem = new MockFileSystem();

        var manifest = DriveManifest.Write(fileSystem, RegistryDirectory(fileSystem));

        await Assert.That(manifest.BackendEndpoint.Host).IsEqualTo("127.0.0.1");
        await Assert.That(manifest.BackendEndpoint.Port).IsNotEqualTo(manifest.UiEndpoint.Port);

        // The upstream endpoint filter also requires an explicit, non-zero port
        // (manifest.ts:17): a reservation that handed back 0 would write a manifest the server
        // rejects at startup. Construction happens to guarantee it today; this asserts it.
        await Assert.That(manifest.BackendEndpoint.Port).IsGreaterThanOrEqualTo(1);
        await Assert.That(manifest.UiEndpoint.Port).IsGreaterThanOrEqualTo(1);
    }
}
