using System.IO.Abstractions;
using System.Text.Json;
using OpenCode.Sdk.Tools.Serialization;
using OpenCode.Sdk.Tools.TestRoster.Models;

namespace OpenCode.Sdk.Tools.TestRoster;

/// <summary>Loads the committed expected roster and refuses any entry that is not a unique
/// "Assembly/target-framework" value.</summary>
internal sealed class TestRosterLoader(IFileSystem fileSystem)
{
    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    public async Task<TestRosterDocument> LoadAsync(string rosterPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rosterPath);

        if (!_fileSystem.File.Exists(rosterPath))
        {
            throw new TestRosterException($"Roster file '{rosterPath}' was not found.");
        }

        var roster = await DeserializeAsync(rosterPath, cancellationToken).ConfigureAwait(false);
        ValidateEntries(rosterPath, roster);
        return roster;
    }

    private async Task<TestRosterDocument> DeserializeAsync(string rosterPath, CancellationToken cancellationToken)
    {
        var stream = _fileSystem.File.OpenRead(rosterPath);
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                return await JsonSerializer.DeserializeAsync(
                           stream,
                           ToolJsonContext.Default.TestRosterDocument,
                           cancellationToken)
                       .ConfigureAwait(false)
                    ?? throw new TestRosterException($"Roster file '{rosterPath}' cannot be JSON null.");
            }
            catch (JsonException exception)
            {
                throw new TestRosterException($"Roster file '{rosterPath}' is not a valid test roster: {exception.Message}", exception);
            }
        }
    }

    /// <summary>Every entry is validated, not only the leg under verification: a malformed entry
    /// anywhere refuses at load instead of waiting for the pass and OS that would reach it.</summary>
    private static void ValidateEntries(string rosterPath, TestRosterDocument roster)
    {
        foreach (var (passName, operatingSystems) in roster.Passes)
        {
            if (operatingSystems is null)
            {
                throw new TestRosterException(
                    $"Roster pass map under pass '{passName}' in '{rosterPath}' cannot be JSON null.");
            }

            foreach (var (operatingSystem, modules) in operatingSystems)
            {
                if (modules is null)
                {
                    throw new TestRosterException(
                        $"Roster module list under pass '{passName}', OS '{operatingSystem}' in '{rosterPath}' cannot be JSON null.");
                }

                var seenModules = new HashSet<string>(StringComparer.Ordinal);
                foreach (var module in modules)
                {
                    if (module is null)
                    {
                        throw new TestRosterException(
                            $"Roster module entry under pass '{passName}', OS '{operatingSystem}' in '{rosterPath}' cannot be JSON null.");
                    }

                    if (!IsModuleEntry(module))
                    {
                        throw new TestRosterException(
                            $"Roster entry '{module}' under pass '{passName}', OS '{operatingSystem}' in '{rosterPath}' "
                            + "is not 'Assembly/target-framework'.");
                    }

                    if (!seenModules.Add(module))
                    {
                        throw new TestRosterException(
                            $"The roster lists module '{module}' twice under pass '{passName}', OS '{operatingSystem}'.");
                    }
                }
            }
        }
    }

    private static bool IsModuleEntry(string module)
    {
        var parts = module.Split('/');
        return parts is [{ Length: > 0 }, { Length: > 0 }];
    }
}
