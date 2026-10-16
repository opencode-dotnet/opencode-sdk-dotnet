using System.Reflection;
using System.Text;

namespace OpenCode.Sdk.Tests.Harness;

/// <summary>Loads embedded fixtures from the compiling test assembly's Fixtures folder.</summary>
internal sealed class FixtureLoader
{
    private readonly Assembly _assembly = typeof(FixtureLoader).Assembly;

    public string LoadJson(string name) => LoadText(name);

    /// <summary>
    /// Every embedded fixture whose name ends with <paramref name="extension"/>, in the form
    /// <see cref="LoadText"/> takes.
    /// </summary>
    public IReadOnlyList<string> Names(string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);

        var prefix = string.Concat(_assembly.GetName().Name, ".Fixtures.");
        var names = new List<string>();
        foreach (var resource in _assembly.GetManifestResourceNames())
        {
            if (resource.StartsWith(prefix, StringComparison.Ordinal) &&
                resource.EndsWith(extension, StringComparison.Ordinal))
            {
                names.Add(resource[prefix.Length..]);
            }
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    public string LoadText(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var resourceName = string.Concat(_assembly.GetName().Name, ".Fixtures.", name);
        var stream = _assembly.GetManifestResourceStream(resourceName)
                     ?? throw new ArgumentException($"Embedded fixture '{name}' was not found.", nameof(name));

        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        var content = Encoding.UTF8.GetString(reader.ReadBytes(checked((int)stream.Length)));
        return content.TrimEnd('\r', '\n');
    }
}
