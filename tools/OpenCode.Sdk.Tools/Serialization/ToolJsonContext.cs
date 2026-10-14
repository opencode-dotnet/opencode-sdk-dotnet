using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Refresh.Models;
using OpenCode.Sdk.Tools.Output;
using OpenCode.Sdk.Tools.TestRoster.Models;

namespace OpenCode.Sdk.Tools.Serialization;

[JsonSourceGenerationOptions(
    AllowTrailingCommas = true,
    GenerationMode = JsonSourceGenerationMode.Metadata,
    NewLine = "\n",
    ReadCommentHandling = JsonCommentHandling.Skip,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(GenerationCuration))]
[JsonSerializable(typeof(GenerationManifest))]
[JsonSerializable(typeof(PatchManifest))]
[JsonSerializable(typeof(SnapshotReceipt))]
[JsonSerializable(typeof(SourceWatch))]
[JsonSerializable(typeof(TestRosterDocument))]
internal sealed partial class ToolJsonContext : JsonSerializerContext;
