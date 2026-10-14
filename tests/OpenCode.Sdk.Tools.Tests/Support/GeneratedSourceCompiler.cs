using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using OpenCode.Sdk.Tools.Generator.Emission;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tools.Tests.Support;

internal static class GeneratedSourceCompiler
{
    private static readonly CSharpParseOptions ParseOptions = new(
        LanguageVersion.CSharp14,
        preprocessorSymbols: ["NET"]);
    private static readonly PortableExecutableReference[] References = CreateReferences();

    /// <summary>
    /// The built-in Roslyn generators an MSBuild compilation of the SDK gets from the targeting
    /// pack and this hand-rolled compilation must register itself: System.Text.Json's, for the
    /// generated serializer contexts, and the LibraryImport generator, for the P/Invoke stubs the
    /// background-service door binds in one interop class (ADR-0026, ADR-0027).
    /// </summary>
    private static readonly AnalyzerFileReference[] SourceGenerators = CreateSourceGeneratorReferences();

    /// <summary>
    /// Hand-written sources sitting <em>above</em> generated output instead of under it: both
    /// PTY families' public doors delegate to generated raw clients (ADR-0021), and the
    /// persistent family's session carries the generated <c>PersistentPtyInfo</c> the server's
    /// attach frame hands it, so unlike the behavior core they cannot compile against a plan that
    /// never emitted those twins. Each rides along only when the plan under test emitted what it
    /// consumes — a consumer listed against several emissions is skipped when any one is missing.
    /// Today's pinned plan emits them all, and a dedicated test asserts it keeps doing so
    /// (<c>SourceEmitterTests.Emit_Should_Produce_Every_GeneratedSurfaceConsumers_RequiredEmission</c>),
    /// so a renamed or dropped twin fails that assertion loudly instead of silently vanishing
    /// from this probe's coverage. A synthetic emitter fixture is free to omit a twin, in which
    /// case its consumers are skipped here rather than failing to compile. The background-service
    /// info probe has its own decoder and depends on no generated model; the stop door's
    /// persistent-terminal shutdown rides the generated <c>persistentPty.shutdown</c> door, so its
    /// shipped implementation and <c>OpenCodeServer</c>, which composes it, ride along with the
    /// persistent family's raw twin: all three of its partial files, because a class compiles only
    /// whole. The handoff sidecar requests its ticket raw through the
    /// pipeline on the persistent family's route, which the route table carries only when that
    /// family is emitted, so it rides the same raw twin.
    /// </summary>
    internal static readonly (string Consumer, string RequiredEmission)[] GeneratedSurfaceConsumers =
    [
        ("Hosting/BackgroundService/Internal/Handoff/ServicePtyHandoff.cs", "PersistentPtys/Internal/PersistentPtysRawClient.cs"),
        ("Hosting/BackgroundService/Internal/Stop/ServicePtyShutdown.cs", "PersistentPtys/Internal/PersistentPtysRawClient.cs"),
        ("Hosting/BackgroundService/OpenCodeServer.BackgroundService.cs", "PersistentPtys/Internal/PersistentPtysRawClient.cs"),
        ("Hosting/Launcher/OpenCodeServer.Launcher.cs", "PersistentPtys/Internal/PersistentPtysRawClient.cs"),
        ("Hosting/OpenCodeServer.cs", "PersistentPtys/Internal/PersistentPtysRawClient.cs"),
        ("Internal/PersistentPtyFrameDecoder.cs", "PersistentPtys/Models/PersistentPtyInfo.cs"),
        ("PersistentPtys/PersistentPtyAttachedFrame.cs", "PersistentPtys/Models/PersistentPtyInfo.cs"),
        ("PersistentPtys/PersistentPtyAttachment.cs", "PersistentPtys/Models/PersistentPtyInfo.cs"),
        ("PersistentPtys/PersistentPtyClient.cs", "PersistentPtys/Internal/PersistentPtyRawClient.cs"),
        ("PersistentPtys/PersistentPtyClient.cs", "PersistentPtys/Models/PersistentPtyInfo.cs"),
        ("PersistentPtys/PersistentPtySession.cs", "PersistentPtys/Models/PersistentPtyInfo.cs"),
        ("PersistentPtys/PersistentPtysClient.cs", "PersistentPtys/Internal/PersistentPtysRawClient.cs"),
        ("PersistentPtys/PersistentPtysClient.cs", "PersistentPtys/Models/PersistentPtyInfo.cs"),
        ("Ptys/PtyClient.cs", "Ptys/Internal/PtyRawClient.cs"),
        ("Ptys/PtysClient.cs", "Ptys/Internal/PtysRawClient.cs"),
    ];

    /// <summary>
    /// One compilation per distinct set of emitted sources for the life of the test process:
    /// every compilation carries the whole SDK, so tests that emit the same plan share one
    /// compilation and one loaded assembly instead of repeating the same work. The tests only
    /// read the loaded assembly's static state, so sharing it changes nothing they observe.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Lazy<CompiledProbe>> Probes = new(StringComparer.Ordinal);

    /// <summary>Serializes the one-time read of the hand-written SDK sources.</summary>
    private static readonly SemaphoreSlim CoreTreesLoad = new(1, 1);

    /// <summary>The experimental ids the SDK suppresses for itself; the compiler reports them as errors by default.</summary>
    private static readonly Dictionary<string, ReportDiagnostic> ExperimentalSuppressions = new(StringComparer.Ordinal)
    {
        ["OPENCODE001"] = ReportDiagnostic.Suppress,
        ["OPENCODE002"] = ReportDiagnostic.Suppress,
        ["OPENCODE003"] = ReportDiagnostic.Suppress,
    };

    /// <summary>The hand-written SDK sources, read and parsed once; each compilation filters them.</summary>
    private static List<CoreTree>? coreTrees;

    /// <summary>
    /// Compiles the generated sources together with the hand-written SDK sources so generated
    /// clients resolve the behavior core; an emitted path shadows its committed twin.
    /// A test that compiles carries <c>[ParallelLimiter&lt;RoslynCompilationSlots&gt;]</c>.
    /// </summary>
    /// <param name="sources">The freshly emitted sources.</param>
    /// <returns>Warning-or-worse diagnostics.</returns>
    public static async Task<Diagnostic[]> CompileWithSdkCoreAsync(IReadOnlyList<GeneratedSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        return (await ProbeAsync(sources)).Diagnostics;
    }

    /// <summary>
    /// Compiles as <see cref="CompileWithSdkCoreAsync"/> does, refuses any warning-or-worse
    /// diagnostic, and returns the loaded assembly.
    /// </summary>
    /// <param name="sources">The freshly emitted sources.</param>
    /// <returns>The compiled assembly, loaded into the default context.</returns>
    public static async Task<Assembly> CompileAndLoadWithSdkCoreAsync(IReadOnlyList<GeneratedSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var probe = await ProbeAsync(sources);
        if (probe.Diagnostics.Length > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine,
                probe.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        }

        return probe.Assembly;
    }

    /// <summary>Compiles a consumer against the generated surface, with or without the experimental opt-in, and returns what it reports.</summary>
    public static async Task<Diagnostic[]> CompileConsumerAsync(IReadOnlyList<GeneratedSource> sources,
        SyntaxTree consumer, bool suppressExperimental)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(consumer);

        var probe = await ProbeAsync(sources);
        if (probe.Diagnostics.Length > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, probe.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        }

        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable);
        if (suppressExperimental)
        {
            options = options.WithSpecificDiagnosticOptions(ExperimentalSuppressions);
        }

        var compilation = CSharpCompilation.Create($"ConsumerProbe_{Guid.NewGuid():N}", [consumer],
            [.. References, MetadataReference.CreateFromImage(probe.Image)], options);
        using var stream = new MemoryStream();
        return [.. compilation.Emit(stream).Diagnostics.Where(static diagnostic => diagnostic.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error)];
    }

    private static async Task<CompiledProbe> ProbeAsync(IReadOnlyList<GeneratedSource> sources)
    {
        var core = await CoreTreesAsync();
        return Probes.GetOrAdd(
            ContentKey(sources),
            static (_, input) => new Lazy<CompiledProbe>(() => CompileProbe(input.Sources, input.Core)),
            (Sources: sources, Core: core)).Value;
    }

    /// <summary>The SHA-256 of every emitted path and its bytes, in emission order.</summary>
    private static string ContentKey(IReadOnlyList<GeneratedSource> sources)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var source in sources)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(source.RelativePath));
            hash.AppendData([0]);
            hash.AppendData(source.Utf8Source.Span);
            hash.AppendData([0]);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static CompiledProbe CompileProbe(IReadOnlyList<GeneratedSource> sources, List<CoreTree> core)
    {
        var emitted = sources.Select(static source => source.RelativePath).ToHashSet(StringComparer.Ordinal);
        var skipped = GeneratedSurfaceConsumers
            .Where(consumer => !emitted.Contains(consumer.RequiredEmission))
            .Select(static consumer => consumer.Consumer)
            .ToHashSet(StringComparer.Ordinal);
        var trees = core
            .Where(tree => !skipped.Contains(tree.Relative))
            .Select(static tree => tree.Tree)
            .ToList();

        var result = Compile(sources, trees);
        if (result.Diagnostics.Length > 0)
        {
            return new CompiledProbe(result.Diagnostics, image: null);
        }

        // The image is emitted now so the probe holds bytes rather than the whole compilation.
        using var stream = new MemoryStream();
        var emittedImage = result.Compilation.Emit(stream);
        if (!emittedImage.Success)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine,
                emittedImage.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        }

        return new CompiledProbe(result.Diagnostics, stream.ToArray());
    }

    private static async Task<List<CoreTree>> CoreTreesAsync()
    {
        if (Volatile.Read(ref coreTrees) is { } loaded)
        {
            return loaded;
        }

        await CoreTreesLoad.WaitAsync();
        try
        {
            return coreTrees ??= await LoadSdkCoreTreesAsync();
        }
        finally
        {
            _ = CoreTreesLoad.Release();
        }
    }

    private static async Task<List<CoreTree>> LoadSdkCoreTreesAsync()
    {
        // The SDK project compiles with implicit usings; the probe replays the same set.
        const string implicitUsings = """
                                      global using System;
                                      global using System.Collections.Generic;
                                      global using System.IO;
                                      global using System.Linq;
                                      global using System.Net.Http;
                                      global using System.Threading;
                                      global using System.Threading.Tasks;
                                      """;
        var fileSystem = new RealFileSystem();
        var root = fileSystem.Path.Combine(AppContext.BaseDirectory, "Fixtures", "SdkSource");
        var trees = new List<CoreTree>
        {
            new("ImplicitUsings.cs", CSharpSyntaxTree.ParseText(implicitUsings, ParseOptions, "sdk:ImplicitUsings.cs", Encoding.UTF8)),
        };
        foreach (var entry in fileSystem
                     .Directory
                     .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                     .Select(path => (Path: path, Relative: fileSystem.Path.GetRelativePath(root, path).Replace('\\', '/')))
                     .OrderBy(static entry => entry.Relative, StringComparer.Ordinal))
        {
            var text = await fileSystem.File.ReadAllTextAsync(entry.Path, CancellationToken.None);
            if (!text.StartsWith("// Generated by OpenCode.Sdk.Tools", StringComparison.Ordinal))
            {
                trees.Add(new CoreTree(
                    entry.Relative,
                    CSharpSyntaxTree.ParseText(text, ParseOptions, $"sdk:{entry.Relative}", Encoding.UTF8)));
            }
        }

        return trees;
    }

    private static CompilationResult Compile(IReadOnlyList<GeneratedSource> sources, IReadOnlyList<SyntaxTree> extraTrees)
    {
        var syntaxTrees = sources
            .Select(static source => CSharpSyntaxTree.ParseText(
                Encoding.UTF8.GetString(source.Utf8Source.Span),
                ParseOptions,
                source.RelativePath,
                Encoding.UTF8))
            .Concat(extraTrees);
        var compilation = CSharpCompilation.Create(
            $"GeneratedSourceProbe_{Guid.NewGuid():N}",
            syntaxTrees,
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable,
                deterministic: true,
                // The SDK project's AllowUnsafeBlocks: the LibraryImport generator emits its stub as unsafe code.
                allowUnsafe: true,
                // The SDK project's NoWarn: the core implements its own experimental families.
                specificDiagnosticOptions: ExperimentalSuppressions));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            SourceGenerators.SelectMany(static reference => reference.GetGenerators(LanguageNames.CSharp)),
            parseOptions: ParseOptions);
        _ = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var generatorDiagnostics);

        Diagnostic[] diagnostics =
        [
            .. generatorDiagnostics
                .Concat(outputCompilation.GetDiagnostics())
                .Where(static diagnostic => diagnostic.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error),
        ];
        return new CompilationResult(outputCompilation, diagnostics);
    }

    private static PortableExecutableReference[] CreateReferences()
    {
        var trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string
                                ?? throw new InvalidOperationException("The runtime did not expose trusted platform assemblies.");
        return
        [
            .. trustedAssemblies
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(static path => MetadataReference.CreateFromFile(path)),
        ];
    }

    private static AnalyzerFileReference[] CreateSourceGeneratorReferences()
    {
        var fileSystem = new RealFileSystem();
        var frameworkDirectory = fileSystem.Path.GetDirectoryName(typeof(JsonSerializer).Assembly.Location)
                                 ?? throw new InvalidOperationException("The runtime framework directory could not be resolved.");
        var frameworkVersion = fileSystem.Path.GetFileName(frameworkDirectory);
        var dotnetRoot = fileSystem.Path.GetFullPath(fileSystem.Path.Combine(frameworkDirectory, "..", "..", ".."));
        var analyzerDirectory = fileSystem.Path.Combine(
            dotnetRoot, "packs", "Microsoft.NETCore.App.Ref", frameworkVersion, "analyzers", "dotnet", "cs");

        // The LibraryImport generator's own dependency sits beside it and is not an analyzer
        // reference of its own; loading it into the default context first is what lets the
        // generator's assembly resolve it, because the probe's loader resolves nothing itself.
        _ = AssemblyLoadContext.Default.LoadFromAssemblyPath(
            GeneratorPath(fileSystem, analyzerDirectory, "Microsoft.Interop.SourceGeneration.dll"));

        return
        [
            new AnalyzerFileReference(
                GeneratorPath(fileSystem, analyzerDirectory, "System.Text.Json.SourceGeneration.dll"),
                new CompilerAnalyzerAssemblyLoader()),
            new AnalyzerFileReference(
                GeneratorPath(fileSystem, analyzerDirectory, "Microsoft.Interop.LibraryImportGenerator.dll"),
                new CompilerAnalyzerAssemblyLoader()),
        ];
    }

    private static string GeneratorPath(RealFileSystem fileSystem, string analyzerDirectory, string fileName)
    {
        var path = fileSystem.Path.Combine(analyzerDirectory, fileName);
        if (!fileSystem.File.Exists(path))
        {
            throw new InvalidOperationException($"The source generator '{fileName}' was not found at '{path}'.");
        }

        return path;
    }

    private sealed record CompilationResult(Compilation Compilation, Diagnostic[] Diagnostics);

    private sealed record CoreTree(string Relative, SyntaxTree Tree);

    /// <summary>A compilation's outcome: its diagnostics and, when it has none, the image, loaded once on first use.</summary>
    private sealed class CompiledProbe
    {
        private readonly Lazy<Assembly>? _assembly;
        private readonly byte[]? _image;

        public CompiledProbe(Diagnostic[] diagnostics, byte[]? image)
        {
            Diagnostics = diagnostics;
            _image = image;
            _assembly = image is null
                ? null
                : new Lazy<Assembly>(() => AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(image, writable: false)));
        }

        public Diagnostic[] Diagnostics { get; }

        public byte[] Image => _image ?? throw new InvalidOperationException("A compilation with diagnostics has no image.");

        public Assembly Assembly => _assembly?.Value
                                    ?? throw new InvalidOperationException("A compilation with diagnostics has no image to load.");
    }
}
