using System.Globalization;
using System.IO.Abstractions;
using System.Text.Json;

namespace OpenCode.Sdk.Tests.Harness;

/// <summary>
/// Writes a named drive-instance manifest (manifest.ts:26-44,70-83 at the pin): the server
/// reads <c>$DRIVE_REGISTRY_DIR/&lt;name&gt;.json</c> when <c>OPENCODE_DRIVE=&lt;name&gt;</c>,
/// and both endpoints must be explicit loopback ws:// ports. The ui endpoint is schema-required
/// but never bound in serve mode; only the backend control server starts.
/// </summary>
internal sealed record DriveManifest(
    string InstanceName,
    string RegistryDirectory,
    Uri BackendEndpoint,
    Uri UiEndpoint)
{
    public static DriveManifest Write(IFileSystem fileSystem, string registryDirectory)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(registryDirectory);

        var name = "sdk" + Guid.NewGuid().ToString("N");
        // Reserved as a pair: the upstream endpoint filter refuses a manifest whose two endpoints
        // share a port, and two independent reservations can hand back the same one.
        var (backendPort, uiPort) = LoopbackPortReservation.ReservePair();
        var backend = "ws://127.0.0.1:" + backendPort.ToString(CultureInfo.InvariantCulture);
        var ui = "ws://127.0.0.1:" + uiPort.ToString(CultureInfo.InvariantCulture);
        _ = fileSystem.Directory.CreateDirectory(registryDirectory);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("endpoints");
            writer.WriteString("ui", ui);
            writer.WriteString("backend", backend);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        fileSystem.File.WriteAllBytes(
            fileSystem.Path.Combine(registryDirectory, name + ".json"), buffer.ToArray());
        return new DriveManifest(name, registryDirectory, new Uri(backend), new Uri(ui));
    }
}
