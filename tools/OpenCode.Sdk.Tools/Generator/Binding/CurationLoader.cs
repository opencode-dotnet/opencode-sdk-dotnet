using System.IO.Abstractions;
using System.Text.Json;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Binding;

internal sealed class CurationLoader(IFileSystem fileSystem)
{
    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    public async Task<GenerationCuration> LoadAsync(string curationPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(curationPath);

        if (!_fileSystem.File.Exists(curationPath))
        {
            throw CreateException(curationPath, "curation file was not found");
        }

        var stream = _fileSystem.File.OpenRead(curationPath);
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                return await JsonSerializer.DeserializeAsync(
                           stream,
                           ToolJsonContext.Default.GenerationCuration,
                           cancellationToken)
                            .ConfigureAwait(false)
                       ?? throw CreateException(curationPath, "curation document cannot be JSON null");
            }
            catch (JsonException exception)
            {
                throw CreateException(curationPath, string.IsNullOrEmpty(exception.Path)
                    ? exception.Message
                    : exception.Message + " (at " + exception.Path + ")");
            }
        }
    }

    private static BindingException CreateException(string subject, string problem) =>
        new(Array.AsReadOnly([new BindingError(BindingErrorCategory.Curation, subject, problem)]));
}
