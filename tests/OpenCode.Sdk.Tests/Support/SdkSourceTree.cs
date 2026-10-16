using OpenCode.Sdk.Tests.Harness;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// The SDK project's source files as the layout guards read them: every <c>.cs</c> file under
/// <c>src/OpenCode.Sdk</c> outside build output, by its project-relative path with forward slashes.
/// </summary>
internal sealed class SdkSourceTree
{
    private readonly RealFileSystem _fileSystem = new();

    public async Task<IReadOnlyList<SdkSourceFile>> LoadAsync()
    {
        var root = _fileSystem.Path.Combine(new PinnedServerCommand(_fileSystem).RepositoryRoot, "src", "OpenCode.Sdk");
        var result = new List<SdkSourceFile>();
        foreach (var path in _fileSystem.Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relativePath = path[(root.Length + 1)..].Replace('\\', '/');
            if (relativePath.StartsWith("bin/", StringComparison.Ordinal) || relativePath.StartsWith("obj/", StringComparison.Ordinal))
            {
                continue;
            }

            using var reader = new StreamReader(_fileSystem.File.OpenRead(path));
            result.Add(new SdkSourceFile(relativePath, (await reader.ReadToEndAsync()).Split('\n')));
        }

        return result;
    }
}
